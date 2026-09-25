using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nebula.Cli.Tests;

/// <summary>
/// An in-process stand-in for the Nebula Cloud API: the endpoints the CLI uses, with scripted answers and a log of
/// every request. It also serves an orchestrator dashboard (/api/state, /api/scale/limits) so local scaling can be
/// tested against the same listener.
/// </summary>
internal sealed class FakeCloud : IDisposable
{
    public sealed record Request(string Method, string Path, string Query, Dictionary<string, string> Headers, string Body)
    {
        public JsonNode? Json => string.IsNullOrWhiteSpace(Body) ? null : JsonNode.Parse(Body);
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        public string? Param(string name) => Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(kv => kv.Split('=', 2))
            .Where(kv => kv[0] == name).Select(kv => Uri.UnescapeDataString(kv.Length > 1 ? kv[1] : "")).FirstOrDefault();
    }

    private readonly HttpListener _listener = new();
    private readonly Thread _thread;
    private volatile bool _stopped;

    public string Url { get; }
    public int Port { get; }
    public List<Request> Requests { get; } = new();

    // --- scripted state ---------------------------------------------------------------------------------
    public string AccessToken = "acc1";
    public string RefreshToken = "ref1";
    public string? NextAccessToken;
    public string? NextRefreshToken;
    /// <summary>Answers for POST /auth/device/token, in order: an error code, or "ok".</summary>
    public Queue<string> DeviceAnswers = new();
    public int DeviceInterval = 0;

    public JsonObject User = J(new { id = "usr_1", email = "dev@example.com", name = "Dev", createdAt = "2026-09-01T00:00:00Z" });
    public List<JsonObject> Orgs = new() { J(new { id = "org_1", slug = "acme", name = "Acme", billingState = "active", spendLimitCents = 10000, createdAt = "2026-09-01T00:00:00Z" }) };
    public Dictionary<string, string> Roles = new() { ["org_1"] = "owner" };
    public List<JsonObject> Projects = new() { J(new { id = "prj_1", organizationId = "org_1", slug = "my-game", name = "My Game", createdAt = "2026-09-01T00:00:00Z", deploymentCount = 1 }) };
    public List<JsonObject> Deployments = new() { Deployment("dep_1", "prj_1", "production", "running", "rel_1") };
    public List<JsonObject> Releases = new() { J(new { id = "rel_1", projectId = "prj_1", number = 1, label = "v1", artifactId = "art_0", sha256 = "aa", nebulaVersion = "0.1.0", protocolVersion = 12, createdAt = "2026-09-01T00:00:00Z" }) };
    public List<JsonObject> Artifacts = new();
    public Dictionary<string, byte[]> Uploads = new();
    public Dictionary<string, (long Size, string Sha)> ArtifactExpectations = new();
    public List<JsonObject> Operations = new();
    private readonly Dictionary<string, int> _eventPolls = new();
    /// <summary>What every new operation ends as: succeeded or failed.</summary>
    public string Outcome = "succeeded";
    /// <summary>Answer every POST /rollouts with 409 naming this operation.</summary>
    public JsonObject? ConflictOperation;
    public JsonObject Status = J(new { });
    public List<JsonObject> LogLines = new();
    public List<JsonObject> StreamLines = new();
    public JsonObject Catalog = J(new
    {
        regions = new[] { new { id = "nyc3", name = "New York 3", available = true } },
        workerSizes = new[] { new { id = "small", vcpus = 2, memoryGb = 4 }, new { id = "medium", vcpus = 4, memoryGb = 8 } },
        gatewaySizes = new[] { new { id = "standard", vcpus = 2, memoryGb = 4 } },
    });
    public JsonObject LocalState = J(new { desiredWorkers = 2, workers = new object[0], scale = new { minWorkers = 1, maxWorkers = 4 } });

    public FakeCloud()
    {
        Port = FreePort();
        Url = $"http://127.0.0.1:{Port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public void Dispose()
    {
        _stopped = true;
        try { _listener.Stop(); _listener.Close(); } catch { }
    }

    public static JsonObject J(object o) => (JsonObject)JsonSerializer.SerializeToNode(o)!;

    public static JsonObject Deployment(string id, string project, string name, string state, string? release, int min = 1, int max = 4) => J(new
    {
        id, projectId = project, organizationId = "org_1", name, region = "nyc3", state,
        workerSize = "small", gatewaySize = "standard", minWorkers = min, maxWorkers = max, minGateways = 1, maxGateways = 3,
        settings = new { }, currentReleaseId = release, previousReleaseId = release != null ? "rel_0" : null,
        gatewayAddress = state == "running" ? "203.0.113.10:7000" : null, webUrl = state == "running" ? "https://203-0-113-10.example.test/" : null,
        dashboardUrl = $"https://cloud.example.test/d/{id}/mesh", createdAt = "2026-09-01T00:00:00Z", updatedAt = "2026-09-01T00:00:00Z",
        lastRolloutAt = (string?)null, spendLimited = false, requireEncryption = false,
        encryption = new { accepted = true, required = false, fingerprint = state == "running" ? new string('0', 0) + string.Concat(Enumerable.Repeat("0f", 32)) : null },
    });

    public IEnumerable<Request> Of(string method, string pathPattern) =>
        Requests.Where(r => r.Method == method && Regex.IsMatch(r.Path, "^" + pathPattern + "$"));

    // --- server ------------------------------------------------------------------------------------------

    private void Loop()
    {
        while (!_stopped)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { return; }
            try { Handle(ctx); }
            catch (Exception e)
            {
                try { Reply(ctx, 500, J(new { error = new { code = "internal", message = e.ToString() } })); } catch { }
            }
        }
    }

    private static void Reply(HttpListenerContext ctx, int status, JsonNode? body)
    {
        ctx.Response.StatusCode = status;
        if (body != null)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes);
        }
        ctx.Response.Close();
    }

    private static JsonObject Error(string code, string message, object? details = null) =>
        J(new { error = new { code, message, details } });

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        string path = req.Url!.AbsolutePath;
        string query = req.Url.Query;
        byte[] raw;
        using (var ms = new MemoryStream()) { req.InputStream.CopyTo(ms); raw = ms.ToArray(); }
        var headers = req.Headers.AllKeys.Where(k => k != null).ToDictionary(k => k!, k => req.Headers[k]!, StringComparer.OrdinalIgnoreCase);
        bool isUpload = path.StartsWith("/upload/");
        var record = new Request(req.HttpMethod, path, query, headers, isUpload ? "" : Encoding.UTF8.GetString(raw));
        lock (Requests) Requests.Add(record);
        var body = record.Json;
        string m = req.HttpMethod;

        // --- the local orchestrator dashboard -------------------------------------------------------------
        if (path == "/api/state") { Reply(ctx, 200, LocalState.DeepClone()); return; }
        if (path == "/api/scale/limits")
        {
            LocalState["scale"] = J(new { minWorkers = (int)body!["min"]!, maxWorkers = (int)body["max"]! });
            Reply(ctx, 200, J(new { ok = true })); return;
        }

        // --- presigned upload ---------------------------------------------------------------------------------
        if (isUpload)
        {
            Uploads[path.Substring("/upload/".Length)] = raw;
            Reply(ctx, 200, null); return;
        }

        if (!path.StartsWith("/v1/")) { Reply(ctx, 404, Error("not_found", "no such route")); return; }
        path = path.Substring(3);

        // --- auth -------------------------------------------------------------------------------------------------
        if (path == "/auth/device" && m == "POST")
        {
            Reply(ctx, 200, J(new { deviceCode = "dev_1", userCode = "WXYZ-1234", verificationUrl = Url + "/device", verificationUrlComplete = Url + "/device?code=WXYZ-1234", expiresIn = 900, interval = DeviceInterval }));
            return;
        }
        if (path == "/auth/device/token" && m == "POST")
        {
            string answer = DeviceAnswers.Count > 0 ? DeviceAnswers.Dequeue() : "ok";
            if (answer == "ok") Reply(ctx, 200, J(new { accessToken = AccessToken, refreshToken = RefreshToken, expiresIn = 3600, user = User }));
            else Reply(ctx, 400, Error(answer, answer));
            return;
        }
        if (path == "/auth/refresh" && m == "POST")
        {
            if (body?["refreshToken"]?.ToString() != RefreshToken) { Reply(ctx, 401, Error("unauthorized", "bad refresh token")); return; }
            if (NextAccessToken != null) AccessToken = NextAccessToken;
            if (NextRefreshToken != null) RefreshToken = NextRefreshToken;
            NextAccessToken = NextRefreshToken = null;
            Reply(ctx, 200, J(new { accessToken = AccessToken, refreshToken = RefreshToken, expiresIn = 3600 }));
            return;
        }
        if (path == "/auth/logout" && m == "POST") { Reply(ctx, 204, null); return; }

        // Everything else needs the bearer token.
        string auth = headers.TryGetValue("Authorization", out var a) ? a : "";
        if (auth != "Bearer " + AccessToken) { Reply(ctx, 401, Error("unauthorized", "bad token")); return; }

        if (path == "/me") { Reply(ctx, 200, J(new { user = User, organizations = Orgs.Select(o => new { organization = o, role = Roles.GetValueOrDefault(o["id"]!.ToString(), "developer") }) })); return; }
        if (path == "/catalog") { Reply(ctx, 200, Catalog.DeepClone()); return; }
        if (path == "/orgs" && m == "POST")
        {
            var org = J(new { id = "org_" + (Orgs.Count + 1), slug = body!["slug"]!.ToString(), name = body["name"]!.ToString(), billingState = "none", spendLimitCents = 0, createdAt = "2026-09-16T00:00:00Z" });
            Orgs.Add(org); Roles[org["id"]!.ToString()] = "owner";
            Reply(ctx, 201, org); return;
        }
        Match mm;
        if ((mm = Regex.Match(path, "^/orgs/([^/]+)/projects$")).Success)
        {
            var org = Orgs.FirstOrDefault(o => o["id"]!.ToString() == mm.Groups[1].Value || o["slug"]!.ToString() == mm.Groups[1].Value);
            if (org == null) { Reply(ctx, 404, Error("not_found", "no such organization")); return; }
            if (m == "POST")
            {
                var p = J(new { id = "prj_" + (Projects.Count + 1), organizationId = org["id"]!.ToString(), slug = body!["slug"]!.ToString(), name = body["name"]!.ToString(), createdAt = "2026-09-16T00:00:00Z", deploymentCount = 0 });
                Projects.Add(p); Reply(ctx, 201, p); return;
            }
            Reply(ctx, 200, J(new { projects = Projects.Where(p => p["organizationId"]!.ToString() == org["id"]!.ToString()) })); return;
        }
        if ((mm = Regex.Match(path, "^/projects/([^/]+)/deployments$")).Success)
        {
            string prj = mm.Groups[1].Value;
            if (m == "POST")
            {
                var d = Deployment("dep_" + (Deployments.Count + 1), prj, body!["name"]!.ToString(), "new", null, (int)body["minWorkers"]!, (int)body["maxWorkers"]!);
                d["region"] = body["region"]!.ToString(); d["workerSize"] = body["workerSize"]!.ToString();
                Deployments.Add(d); Reply(ctx, 201, d); return;
            }
            Reply(ctx, 200, J(new { deployments = Deployments.Where(d => d["projectId"]!.ToString() == prj) })); return;
        }
        if ((mm = Regex.Match(path, "^/projects/([^/]+)/artifacts$")).Success && m == "POST")
        {
            string sha = body!["sha256"]!.ToString();
            long size = (long)body["sizeBytes"]!;
            var existing = Artifacts.FirstOrDefault(x => x["sha256"]!.ToString() == sha && x["state"]!.ToString() == "verified");
            if (existing != null) { Reply(ctx, 201, J(new { artifact = existing, upload = (object?)null })); return; }
            var art = J(new { id = "art_" + (Artifacts.Count + 1), projectId = mm.Groups[1].Value, kind = "linux-server", sha256 = sha, sizeBytes = size, state = "pending", createdAt = "2026-09-16T00:00:00Z" });
            Artifacts.Add(art);
            ArtifactExpectations[art["id"]!.ToString()] = (size, sha);
            Reply(ctx, 201, J(new { artifact = art, upload = new { method = "PUT", url = Url + "/upload/" + art["id"], headers = new Dictionary<string, string> { ["Content-Type"] = "application/gzip" }, expiresAt = "2026-09-16T01:00:00Z" } }));
            return;
        }
        if ((mm = Regex.Match(path, "^/projects/([^/]+)/artifacts/([^/]+)/complete$")).Success && m == "POST")
        {
            var art = Artifacts.First(x => x["id"]!.ToString() == mm.Groups[2].Value);
            var (size, sha) = ArtifactExpectations[mm.Groups[2].Value];
            if (art["state"]!.ToString() == "verified") { Reply(ctx, 200, art); return; }
            if (!Uploads.TryGetValue(mm.Groups[2].Value, out var bytes) || bytes.Length != size)
            {
                art["state"] = "failed"; art["error"] = bytes == null ? "object not found" : "size mismatch";
                Reply(ctx, 400, Error("invalid", "artifact verification failed: " + art["error"])); return;
            }
            // The real API answers 202 "verifying" and settles in the background; the fake settles at once.
            bool ok = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() == sha;
            art["state"] = ok ? "verified" : "failed"; art["error"] = ok ? null : "sha256 mismatch";
            Reply(ctx, 202, art); return;
        }
        if ((mm = Regex.Match(path, "^/projects/([^/]+)/artifacts/([^/]+)$")).Success && m == "GET")
        {
            Reply(ctx, 200, Artifacts.First(x => x["id"]!.ToString() == mm.Groups[2].Value)); return;
        }
        if ((mm = Regex.Match(path, "^/projects/([^/]+)/releases$")).Success && m == "POST")
        {
            var rel = J(new
            {
                id = "rel_" + (Releases.Count + 1), projectId = mm.Groups[1].Value, number = Releases.Count + 1, label = body!["label"]?.ToString(),
                artifactId = body["artifactId"]!.ToString(), nebulaVersion = body["nebulaVersion"]?.ToString(), protocolVersion = body["protocolVersion"]?.GetValue<int>(),
                minProtocolVersion = body["minProtocolVersion"]?.GetValue<int>(), gameContentVersion = body["gameContentVersion"]?.GetValue<long>(), minGameContentVersion = body["minGameContentVersion"]?.GetValue<long>(),
                git = body["git"]?.DeepClone(), notes = body["notes"]?.ToString(), createdBy = "usr_1", createdAt = "2026-09-16T00:00:00Z", immutable = true,
            });
            Releases.Add(rel); Reply(ctx, 201, rel); return;
        }
        if ((mm = Regex.Match(path, "^/releases/([^/]+)$")).Success)
        {
            var rel = Releases.FirstOrDefault(r => r["id"]!.ToString() == mm.Groups[1].Value);
            if (rel == null) Reply(ctx, 404, Error("not_found", "no such release")); else Reply(ctx, 200, rel);
            return;
        }
        if ((mm = Regex.Match(path, "^/deployments/([^/]+)$")).Success)
        {
            var d = Deployments.FirstOrDefault(x => x["id"]!.ToString() == mm.Groups[1].Value);
            if (d == null) { Reply(ctx, 404, Error("not_found", "no such deployment")); return; }
            if (m == "PATCH")
            {
                foreach (var kv in body!.AsObject()) d[kv.Key] = kv.Value?.DeepClone();
                // As the API: the deployment wrapped, with the reconfigure operation when one was started (none here).
                Reply(ctx, 200, J(new { deployment = d, operation = (object?)null })); return;
            }
            if (m == "DELETE")
            {
                d["state"] = "destroying";
                var op = NewOperation(d["id"]!.ToString(), "destroy", new[] { "drain", "delete-machines", "delete-database" });
                Reply(ctx, 202, J(new { operation = op })); return;
            }
            Reply(ctx, 200, d); return;
        }
        if ((mm = Regex.Match(path, "^/deployments/([^/]+)/(rollouts|rollback|scale|status|logs|logs/stream|operations)$")).Success)
        {
            var d = Deployments.FirstOrDefault(x => x["id"]!.ToString() == mm.Groups[1].Value);
            if (d == null) { Reply(ctx, 404, Error("not_found", "no such deployment")); return; }
            string what = mm.Groups[2].Value;
            switch (what)
            {
                case "rollouts" when m == "POST":
                    if (ConflictOperation != null) { if (!Operations.Contains(ConflictOperation)) Operations.Add(ConflictOperation); Reply(ctx, 409, J(new { error = new { code = "conflict", message = "a rollout is already running", details = new { operationId = ConflictOperation["id"]!.ToString() } }, operation = ConflictOperation })); return; }
                    d["currentReleaseId"] = body!["releaseId"]!.ToString(); d["state"] = "running";
                    Reply(ctx, 202, J(new { operation = NewOperation(d["id"]!.ToString(), "rollout", new[] { "upload-build", "restart-orchestrator", "wait-ready" }) })); return;
                case "rollback" when m == "POST":
                    d["currentReleaseId"] = body?["releaseId"]?.ToString() ?? d["previousReleaseId"]?.ToString();
                    Reply(ctx, 202, J(new { operation = NewOperation(d["id"]!.ToString(), "rollback", new[] { "restart-orchestrator", "wait-ready" }) })); return;
                case "scale" when m == "POST":
                    foreach (var kv in body!.AsObject()) if (kv.Value != null) d[kv.Key] = kv.Value.DeepClone();
                    Reply(ctx, 200, d); return;
                case "status":
                    var st = Status.DeepClone().AsObject();
                    st["deployment"] ??= d.DeepClone();
                    Reply(ctx, 200, st); return;
                case "operations":
                    // The conflicting operation is not in the list: the CLI must learn about it from the 409 itself.
                    Reply(ctx, 200, J(new { operations = Operations.Where(o => o != ConflictOperation && o["deploymentId"]!.ToString() == d["id"]!.ToString()).Reverse() })); return;
                case "logs":
                    Reply(ctx, 200, J(new { lines = LogLines, next = (string?)null })); return;
                case "logs/stream":
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.SendChunked = true;
                    foreach (var line in StreamLines)
                    {
                        var bytes = Encoding.UTF8.GetBytes("data: " + line.ToJsonString() + "\n\n");
                        ctx.Response.OutputStream.Write(bytes);
                        ctx.Response.OutputStream.Flush();
                    }
                    ctx.Response.Close();
                    return;
            }
        }
        if ((mm = Regex.Match(path, "^/operations/([^/]+)$")).Success)
        {
            var op = Operations.FirstOrDefault(o => o["id"]!.ToString() == mm.Groups[1].Value);
            if (op == null) Reply(ctx, 404, Error("not_found", "no such operation")); else Reply(ctx, 200, op);
            return;
        }
        if ((mm = Regex.Match(path, "^/operations/([^/]+)/events$")).Success)
        {
            var op = Operations.FirstOrDefault(o => o["id"]!.ToString() == mm.Groups[1].Value);
            if (op == null) { Reply(ctx, 404, Error("not_found", "no such operation")); return; }
            long after = long.Parse(Regex.Match(query, "after=(\\d+)").Groups[1].Value is { Length: > 0 } s ? s : "0");
            Reply(ctx, 200, Advance(op, after)); return;
        }
        if ((mm = Regex.Match(path, "^/projects/([^/]+)/env(?:/([^/]+))?$")).Success)
        {
            HandleEnv(ctx, m, mm.Groups[1].Value, mm.Groups[2].Success ? Uri.UnescapeDataString(mm.Groups[2].Value) : null, record, body);
            return;
        }
        Reply(ctx, 404, Error("not_found", $"{m} {path} is not implemented by the fake"));
    }

    // --- environment variables --------------------------------------------------------------------------------

    /// <summary>Stored variables per "project/key", with their real value (a secret's never goes back out).</summary>
    public Dictionary<string, (string Value, bool Secret, List<string> Deployments)> Env = new();

    private static readonly Regex EnvKey = new("^[A-Za-z_][A-Za-z0-9_]*$");

    private static JsonObject EnvView(string key, (string Value, bool Secret, List<string> Deployments) v) => J(new
    {
        key, secret = v.Secret, value = v.Secret ? null : v.Value, deployments = v.Deployments, updatedAt = "2026-09-20T10:00:00Z", updatedBy = "dev@example.com",
    });

    private static string? EnvRefusal(string key, string? value)
    {
        if (!EnvKey.IsMatch(key)) return "invalid_key";
        if (key.Equals("PORT", StringComparison.OrdinalIgnoreCase) || key.StartsWith("NEBULA_", StringComparison.OrdinalIgnoreCase)) return "reserved_key";
        if (value != null && Encoding.UTF8.GetByteCount(value) > 32 * 1024) return "value_too_long";
        return null;
    }

    private void HandleEnv(HttpListenerContext ctx, string m, string project, string? key, Request req, JsonNode? body)
    {
        IEnumerable<KeyValuePair<string, (string Value, bool Secret, List<string> Deployments)>> Of() => Env.Where(e => e.Key.StartsWith(project + "/"));
        if (key == null && m == "GET")
        {
            string? dep = req.Param("deployment");
            var list = Of().Where(e => dep == null || e.Value.Deployments.Count == 0 || e.Value.Deployments.Contains(dep))
                .Select(e => EnvView(e.Key.Substring(project.Length + 1), e.Value)).ToList();
            Reply(ctx, 200, J(new { vars = list })); return;
        }
        if (key == null && m == "PUT")
        {
            var deployments = body!["deployments"]!.AsArray().Select(d => d!.ToString()).ToList();
            var inputs = body["vars"]!.AsArray().Select(v => (Key: v!["key"]!.ToString(), Value: v["value"]!.ToString(), Secret: (bool)v["secret"]!)).ToList();
            foreach (var i in inputs)
                if (EnvRefusal(i.Key, i.Value) is { } bad) { Reply(ctx, 400, J(new { error = bad })); return; }
            foreach (var i in inputs) Env[project + "/" + i.Key] = (i.Value, i.Secret, deployments.ToList());
            Reply(ctx, 200, J(new { vars = inputs.Select(i => EnvView(i.Key, Env[project + "/" + i.Key])).ToList() })); return;
        }
        if (key != null && m == "PUT")
        {
            string value = body!["value"]!.ToString();
            if (EnvRefusal(key, value) is { } bad) { Reply(ctx, 400, J(new { error = bad })); return; }
            var v = (value, (bool)body["secret"]!, body["deployments"]!.AsArray().Select(d => d!.ToString()).ToList());
            Env[project + "/" + key] = v;
            Reply(ctx, 200, EnvView(key, v)); return;
        }
        if (key != null && m == "DELETE")
        {
            if (!Env.TryGetValue(project + "/" + key, out var v)) { Reply(ctx, 404, Error("not_found", $"{key} is not set")); return; }
            string? dep = req.Param("deployment");
            if (dep == null || (v.Deployments.Remove(dep) && v.Deployments.Count == 0)) Env.Remove(project + "/" + key);
            Reply(ctx, 204, null); return;
        }
        Reply(ctx, 405, Error("method_not_allowed", m));
    }

    private JsonObject NewOperation(string deploymentId, string kind, string[] steps)
    {
        var op = J(new
        {
            id = "op_" + (Operations.Count + 1), deploymentId, kind, state = "running", createdBy = "usr_1", createdAt = "2026-09-16T10:00:00Z", startedAt = "2026-09-16T10:00:00Z",
            steps = steps.Select((s, i) => new { name = s, state = i == 0 ? "running" : "pending" }),
        });
        Operations.Add(op);
        return op;
    }

    /// <summary>Every poll moves the scripted operation on: first poll finishes step 1, the second one ends it.</summary>
    private JsonObject Advance(JsonObject op, long after)
    {
        string id = op["id"]!.ToString();
        int poll = _eventPolls.GetValueOrDefault(id) + 1;
        _eventPolls[id] = poll;
        var steps = op["steps"]!.AsArray();
        var events = new List<object>();
        if (poll == 1)
        {
            steps[0]!["state"] = "succeeded";
            if (steps.Count > 1) steps[1]!["state"] = "running";
            events.Add(new { seq = 1, at = "2026-09-16T10:00:01Z", level = "info", step = steps[0]!["name"]!.ToString(), message = "done" });
            events.Add(new { seq = 2, at = "2026-09-16T10:00:02Z", level = "info", step = steps.Count > 1 ? steps[1]!["name"]!.ToString() : null, message = "working" });
        }
        else if (op["state"]!.ToString() == "running")
        {
            for (int i = 1; i < steps.Count; i++) steps[i]!["state"] = Outcome == "failed" && i == steps.Count - 1 ? "failed" : "succeeded";
            op["state"] = Outcome;
            op["finishedAt"] = "2026-09-16T10:01:00Z";
            if (Outcome == "failed")
            {
                op["error"] = J(new { code = "unhealthy", message = "the orchestrator never became ready" });
                steps[^1]!["message"] = "timed out after 300s";
                events.Add(new { seq = 3, at = "2026-09-16T10:01:00Z", level = "error", step = steps[^1]!["name"]!.ToString(), message = "orchestrator did not answer" });
            }
            else
            {
                events.Add(new { seq = 3, at = "2026-09-16T10:01:00Z", level = "info", step = (string?)null, message = "rollout complete" });
                var d = Deployments.FirstOrDefault(x => x["id"]!.ToString() == op["deploymentId"]!.ToString());
                if (d != null && op["kind"]!.ToString() == "destroy") { d["state"] = "destroyed"; d["gatewayAddress"] = null; }
            }
        }
        return J(new { events = events.Where(e => (int)JsonSerializer.SerializeToNode(e)!["seq"]! > after), operation = op });
    }
}
