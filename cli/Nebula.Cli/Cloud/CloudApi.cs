using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nebula.Cli.Cloud;

using Nebula.Cli.Core;

/// <summary>An error the Nebula Cloud API answered: the HTTP status, the error code and its message.</summary>
public sealed class CloudApiError : CliError
{
    public int Status { get; }
    public string Code { get; }
    public JsonNode? Details { get; }
    /// <summary>The whole body, for answers that carry more than the error envelope (a 409 names the running operation).</summary>
    public JsonNode? Body { get; }

    public CloudApiError(int status, string code, string message, string? hint, JsonNode? details, JsonNode? body)
        : base(message, hint, status == 401 ? 3 : 1)
    {
        Status = status;
        Code = code;
        Details = details;
        Body = body;
    }
}

/// <summary>
/// The Nebula Cloud developer API (https://api.nebula.1by3.co/v1). Bearer tokens from `nebula cloud login`, refreshed
/// and re-saved when they expire; an Idempotency-Key on every mutating call so a retry never repeats an action.
/// </summary>
public sealed class CloudApi
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>One random key per CLI invocation; each mutating call adds its own name so retries replay, not repeat.</summary>
    public static readonly string InvocationId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <summary>How much longer the device-login poll waits after a slow_down answer (RFC 8628 says 5 s).</summary>
    internal static TimeSpan SlowDownStep = TimeSpan.FromSeconds(5);

    public static string UserAgent => $"nebula-cli/{Platform.CliVersion}";

    private readonly HttpClient _http;
    private readonly CliConfig? _config;
    public string BaseUrl { get; }
    public CliConfig.CloudSettings? Session => _config?.Cloud;

    private CloudApi(string baseUrl, CliConfig? config)
    {
        BaseUrl = baseUrl;
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>A client without credentials, for the login flow.</summary>
    public static CloudApi Anonymous(string apiUrl) => new(apiUrl, null);

    /// <summary>The logged-in client, or a CliError telling the user to log in.</summary>
    public static CloudApi Require(Context ctx)
    {
        var cloud = ctx.Config.Cloud;
        if (cloud == null || !cloud.IsLoggedIn)
            throw new CliError("you are not logged in to Nebula Cloud", "run `nebula cloud login`", 3);
        return new CloudApi(CliConfig.CloudSettings.ResolveApiUrl(cloud), ctx.Config);
    }

    // --- transport ------------------------------------------------------------------------------------------

    private sealed record RawResponse(int Status, JsonNode? Body, string Text);

    private HttpRequestMessage Build(HttpMethod method, string path, object? body, string? idempotency, bool auth)
    {
        var req = new HttpRequestMessage(method, BaseUrl + "/v1" + path);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        if (idempotency != null) req.Headers.Add("Idempotency-Key", $"{InvocationId}-{idempotency}");
        if (auth && Session?.AccessToken is { Length: > 0 } token) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private RawResponse SendRaw(HttpMethod method, string path, object? body, string? idempotency, bool auth, CancellationToken ct)
    {
        Ui.Verbose($"cloud {method} {path}");
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) Thread.Sleep(TimeSpan.FromSeconds(attempt));
            using var req = Build(method, path, body, idempotency, auth);
            HttpResponseMessage resp;
            try { resp = _http.SendAsync(req, ct).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                last = e;
                Ui.Verbose($"cloud {method} {path}: {e.Message} (attempt {attempt + 1})");
                continue;
            }
            using (resp)
            {
                string text = resp.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                int status = (int)resp.StatusCode;
                // Transient server trouble is retried; the idempotency key keeps a retried mutation from repeating.
                if (status is 500 or 503 or 504 && attempt < 2 && (method == HttpMethod.Get || idempotency != null))
                {
                    last = new HttpRequestException($"{status} from {path}");
                    continue;
                }
                JsonNode? node = null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try { node = JsonNode.Parse(text); } catch (JsonException) { }
                }
                return new RawResponse(status, node, text);
            }
        }
        throw new CliError($"could not reach the Nebula Cloud API at {BaseUrl}: {last?.Message}", "check your network, or NEBULA_CLOUD_API if you set it");
    }

    /// <summary>Send with bearer auth, refreshing an expired access token once; throws <see cref="CloudApiError"/> on any error answer.</summary>
    private JsonNode Send(HttpMethod method, string path, object? body = null, string? idempotency = null, bool auth = true, CancellationToken ct = default)
    {
        if (auth) RefreshIfExpiring();
        var r = SendRaw(method, path, body, idempotency, auth, ct);
        if (r.Status == 401 && auth && Session?.RefreshToken is { Length: > 0 } && TryRefresh())
            r = SendRaw(method, path, body, idempotency, auth, ct);
        if (r.Status >= 400) throw ToError(r, path);
        return r.Body ?? new JsonObject();
    }

    private T Send<T>(HttpMethod method, string path, object? body = null, string? idempotency = null, CancellationToken ct = default) =>
        Send(method, path, body, idempotency, true, ct).Deserialize<T>(Json) ?? throw new CliError($"the Cloud API answered {path} without a body");

    private static T As<T>(JsonNode? node, string what) => node.Deserialize<T>(Json) ?? throw new CliError($"the Cloud API answered without {what}");

    private CloudApiError ToError(RawResponse r, string path)
    {
        var err = r.Body?["error"];
        string code = err?["code"]?.ToString() ?? r.Status switch { 401 => "unauthorized", 403 => "forbidden", 404 => "not_found", 409 => "conflict", 429 => "rate_limited", _ => "error" };
        string message = err?["message"]?.ToString() ?? (string.IsNullOrWhiteSpace(r.Text) ? $"HTTP {r.Status}" : r.Text.Trim());
        string? hint = r.Status switch
        {
            401 => Session?.IsLoggedIn == true ? "your session has expired; run `nebula cloud login` again" : "run `nebula cloud login`",
            402 => "the organization reached its spend limit; raise it in the Cloud Dashboard (Billing), or scale the deployment in",
            403 => "your role in the organization does not allow this; ask an owner or admin",
            404 => "check the organization, project, and deployment in nebula.json (`nebula deployments` lists them)",
            426 => $"this CLI is too old for the Cloud API{(err?["minimumCliVersion"]?.ToString() is { Length: > 0 } v ? $" (minimum {v})" : "")}; run `nebula setup` to update it",
            429 => "the API is rate limiting this organization; wait a moment and try again",
            502 => "the hosting provider answered with an error; try again in a minute, then check `nebula status --cloud`",
            _ => null,
        };
        return new CloudApiError(r.Status, code, $"Nebula Cloud: {message} ({code}, HTTP {r.Status} on {path})", hint, err?["details"], r.Body);
    }

    // --- authentication ---------------------------------------------------------------------------------------

    public sealed record DeviceStart(string DeviceCode, string UserCode, string VerificationUrl, string? VerificationUrlComplete, int ExpiresIn, int Interval);
    public sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresIn, User? User);

    public DeviceStart StartDeviceLogin() =>
        As<DeviceStart>(Send(HttpMethod.Post, "/auth/device", new { client = UserAgent }, auth: false), "a device code");

    /// <summary>Poll until the user approves the code in the browser. Honours interval and slow_down.</summary>
    public TokenResponse WaitForDeviceToken(DeviceStart start, CancellationToken ct = default)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(0, start.Interval));
        var deadline = DateTime.UtcNow.AddSeconds(start.ExpiresIn > 0 ? start.ExpiresIn : 900);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (interval > TimeSpan.Zero) Thread.Sleep(interval);
            var r = SendRaw(HttpMethod.Post, "/auth/device/token", new { deviceCode = start.DeviceCode }, null, false, ct);
            if (r.Status < 400) return As<TokenResponse>(r.Body, "tokens");
            string code = r.Body?["error"]?["code"]?.ToString() ?? "";
            switch (code)
            {
                case "authorization_pending": break;
                case "slow_down": interval += SlowDownStep; break;
                case "expired_token": throw new CliError("the login code expired before it was approved", "run `nebula cloud login` again");
                case "access_denied": throw new CliError("the login was denied in the browser");
                default: throw ToError(r, "/auth/device/token");
            }
            if (DateTime.UtcNow > deadline) throw new CliError("the login code expired before it was approved", "run `nebula cloud login` again");
        }
    }

    /// <summary>Store a fresh token set in the config (and on disk).</summary>
    public static void SaveSession(CliConfig config, string apiUrl, TokenResponse tokens)
    {
        var s = config.Cloud ?? new CliConfig.CloudSettings();
        s.ApiUrl = apiUrl;
        s.AccessToken = tokens.AccessToken;
        s.RefreshToken = tokens.RefreshToken;
        s.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);
        if (tokens.User != null) s.User = new CliConfig.CloudSettings.CloudUser { Id = tokens.User.Id, Email = tokens.User.Email, Name = tokens.User.Name };
        s.LoggedInAt = DateTime.UtcNow.ToString("o");
        config.Cloud = s;
        config.Save();
    }

    private void RefreshIfExpiring()
    {
        var s = Session;
        if (s?.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow.AddSeconds(30) && !string.IsNullOrEmpty(s.RefreshToken)) TryRefresh();
    }

    /// <summary>Rotate the refresh token; false when the API refuses it (the user must log in again).</summary>
    private bool TryRefresh()
    {
        var s = Session;
        if (_config == null || s?.RefreshToken is not { Length: > 0 } refresh) return false;
        Ui.Verbose("cloud: refreshing the access token");
        var r = SendRaw(HttpMethod.Post, "/auth/refresh", new { refreshToken = refresh }, null, false, default);
        if (r.Status >= 400 || r.Body == null) return false;
        var t = r.Body.Deserialize<TokenResponse>(Json);
        if (t == null || string.IsNullOrEmpty(t.AccessToken)) return false;
        s.AccessToken = t.AccessToken;
        if (!string.IsNullOrEmpty(t.RefreshToken)) s.RefreshToken = t.RefreshToken;
        s.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(t.ExpiresIn > 0 ? t.ExpiresIn : 3600);
        _config.Save();
        return true;
    }

    /// <summary>Revoke the refresh token. Errors are ignored: the local credentials go either way.</summary>
    public void Logout()
    {
        if (Session?.RefreshToken is not { Length: > 0 } refresh) return;
        try { SendRaw(HttpMethod.Post, "/auth/logout", new { refreshToken = refresh }, null, true, default); }
        catch (CliError e) { Ui.Verbose("logout: " + e.Message); }
    }

    // --- me / organizations / projects ---------------------------------------------------------------------------

    public sealed record User(string Id, string? Email, string? Name, string? AvatarUrl, string? CreatedAt);
    public sealed record Organization(string Id, string Slug, string Name, string? CreatedAt, string? BillingState, long? SpendLimitCents);
    public sealed record Membership(Organization Organization, string Role);
    public sealed record Me(User User, List<Membership> Organizations);
    public sealed record Project(string Id, string OrganizationId, string Slug, string Name, string? CreatedAt, int DeploymentCount);

    public Me GetMe() => Send<Me>(HttpMethod.Get, "/me");
    public Organization GetOrganization(string idOrSlug) => Send<Organization>(HttpMethod.Get, $"/orgs/{Uri.EscapeDataString(idOrSlug)}");
    public Organization CreateOrganization(string name, string slug) => Send<Organization>(HttpMethod.Post, "/orgs", new { name, slug }, "create-org-" + slug);
    public List<Project> Projects(string org) => As<List<Project>>(Send(HttpMethod.Get, $"/orgs/{Uri.EscapeDataString(org)}/projects")["projects"], "projects");
    public Project CreateProject(string org, string name, string slug) => Send<Project>(HttpMethod.Post, $"/orgs/{Uri.EscapeDataString(org)}/projects", new { name, slug }, "create-project-" + slug);

    // --- artifacts and releases ------------------------------------------------------------------------------------

    public sealed record Artifact(string Id, string ProjectId, string Kind, string Sha256, long SizeBytes, string State, string? CreatedAt, string? VerifiedAt, string? Error = null);
    public sealed record ArtifactUpload(string Method, string Url, Dictionary<string, string>? Headers, string? ExpiresAt);
    public sealed record ArtifactCreated(Artifact Artifact, ArtifactUpload? Upload);
    public sealed record GitInfo(string? Commit, string? Branch, bool Dirty);
    public sealed record Release(string Id, string ProjectId, int Number, string? Label, string ArtifactId, string? Sha256, string? NebulaVersion, int? ProtocolVersion, GitInfo? Git, string? Notes, System.Text.Json.JsonElement? CreatedBy, string? CreatedAt);

    public ArtifactCreated CreateArtifact(string project, string sha256, long sizeBytes, string fileName, string? md5 = null) =>
        Send<ArtifactCreated>(HttpMethod.Post, $"/projects/{project}/artifacts", new { kind = "linux-server", sha256, md5, sizeBytes, fileName }, "artifact-" + sha256);

    /// <summary>Ask the API to verify what is in storage. 202 "verifying" (poll <see cref="GetArtifact"/>), 200 "verified", or a 400 <see cref="CloudApiError"/> when nothing usable is there.</summary>
    public Artifact CompleteArtifact(string project, string artifact) =>
        Send<Artifact>(HttpMethod.Post, $"/projects/{project}/artifacts/{artifact}/complete", new { });

    public Artifact GetArtifact(string project, string artifact) => Send<Artifact>(HttpMethod.Get, $"/projects/{project}/artifacts/{artifact}");

    public Release CreateRelease(string project, string artifactId, JsonNode manifest, string? label, string? notes, string nebulaVersion, int? protocolVersion, GitInfo? git) =>
        Send<Release>(HttpMethod.Post, $"/projects/{project}/releases",
            new { artifactId, manifest, label, notes, nebulaVersion, protocolVersion, git }, "release-" + artifactId);

    public Release GetRelease(string release) => Send<Release>(HttpMethod.Get, $"/releases/{Uri.EscapeDataString(release)}");
    public List<Release> Releases(string project, int limit = 20) => As<List<Release>>(Send(HttpMethod.Get, $"/projects/{project}/releases?limit={limit}")["releases"], "releases");

    /// <summary>PUT the file to the presigned URL. No bearer token: the URL carries its own authorization.</summary>
    public void Upload(ArtifactUpload upload, string file, Action<long, long>? progress = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        using var fs = File.OpenRead(file);
        long total = fs.Length;
        using var content = new StreamContent(new ProgressStream(fs, sent => progress?.Invoke(sent, total)));
        content.Headers.ContentLength = total;
        using var req = new HttpRequestMessage(new HttpMethod(string.IsNullOrEmpty(upload.Method) ? "PUT" : upload.Method), upload.Url) { Content = content };
        foreach (var (k, v) in upload.Headers ?? new Dictionary<string, string>())
        {
            if (!content.Headers.TryAddWithoutValidation(k, v)) req.Headers.TryAddWithoutValidation(k, v);
        }
        HttpResponseMessage resp;
        try { resp = http.SendAsync(req, ct).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { throw new CliError($"upload failed: {e.Message}", "check your network and run `nebula deploy` again"); }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                string text = resp.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                throw new CliError($"upload rejected ({(int)resp.StatusCode}): {Truncate(text, 300)}", "run `nebula deploy` again; the upload URL may have expired");
            }
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";

    private sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long> _report;
        private long _sent;
        public ProgressStream(Stream inner, Action<long> report) { _inner = inner; _report = report; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(_inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await _inner.ReadAsync(buffer, ct));
        private int Count(int n) { if (n > 0) { _sent += n; _report(_sent); } return n; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // --- deployments ----------------------------------------------------------------------------------------------

    public sealed record Deployment(
        string Id, string ProjectId, string? OrganizationId, string Name, string? Region, string State,
        string? WorkerSize, string? GatewaySize, int? MinWorkers, int? MaxWorkers, int? MinGateways, int? MaxGateways,
        JsonElement? Settings, string? CurrentReleaseId, string? PreviousReleaseId, string? GatewayAddress, string? WebUrl,
        string? DashboardUrl, string? CreatedAt, string? UpdatedAt, string? LastRolloutAt, bool SpendLimited);

    public sealed record NewDeployment(string Name, string Region, string WorkerSize, string GatewaySize, int MinWorkers, int MaxWorkers, int MinGateways, int MaxGateways, Dictionary<string, string>? Settings);

    public List<Deployment> Deployments(string project) => As<List<Deployment>>(Send(HttpMethod.Get, $"/projects/{project}/deployments")["deployments"], "deployments");
    public Deployment GetDeployment(string deployment) => Send<Deployment>(HttpMethod.Get, $"/deployments/{deployment}");
    public Deployment CreateDeployment(string project, NewDeployment d) => Send<Deployment>(HttpMethod.Post, $"/projects/{project}/deployments", d, "create-deployment-" + d.Name);
    public Deployment PatchDeployment(string deployment, object patch) => Send<Deployment>(HttpMethod.Patch, $"/deployments/{deployment}", patch, "patch-deployment");
    public Deployment Scale(string deployment, int? minWorkers, int? maxWorkers, int? minGateways, int? maxGateways) =>
        Send<Deployment>(HttpMethod.Post, $"/deployments/{deployment}/scale", new { minWorkers, maxWorkers, minGateways, maxGateways }, "scale");

    public Operation DeleteDeployment(string deployment) => OperationOf(Send(HttpMethod.Delete, $"/deployments/{deployment}", null, "destroy"));
    public Operation Rollout(string deployment, string releaseId, bool allowProtocolChange) =>
        OperationOf(Send(HttpMethod.Post, $"/deployments/{deployment}/rollouts", new { releaseId, allowProtocolChange }, "rollout-" + releaseId));
    public Operation Rollback(string deployment, string? releaseId) =>
        OperationOf(Send(HttpMethod.Post, $"/deployments/{deployment}/rollback", new { releaseId }, "rollback"));

    private static Operation OperationOf(JsonNode body) => As<Operation>(body["operation"] ?? body, "an operation");

    // --- catalog ----------------------------------------------------------------------------------------------------

    public sealed record Region(string Id, string? Name, bool Available);
    public sealed record MachineSize(string Id, int? Vcpus, double? MemoryGb);
    public sealed record Catalog(List<Region>? Regions, List<MachineSize>? WorkerSizes, List<MachineSize>? GatewaySizes);

    public Catalog GetCatalog() => Send<Catalog>(HttpMethod.Get, "/catalog");

    // --- operations -------------------------------------------------------------------------------------------------

    public sealed record OperationError(string? Code, string? Message);
    public sealed record OperationStep(string Name, string State, string? StartedAt, string? FinishedAt, string? Message);
    public sealed record Operation(string Id, string? DeploymentId, string Kind, string State, System.Text.Json.JsonElement? CreatedBy, string? CreatedAt, string? StartedAt, string? FinishedAt, OperationError? Error, List<OperationStep>? Steps)
    {
        public bool IsFinished => State is "succeeded" or "failed" or "cancelled";
        public bool IsActive => State is "pending" or "running";
    }
    public sealed record OperationEvent(long Seq, string? At, string? Level, string? Step, string? Message);
    public sealed record OperationEvents(List<OperationEvent>? Events, Operation Operation);

    public Operation GetOperation(string operation) => Send<Operation>(HttpMethod.Get, $"/operations/{operation}");
    public List<Operation> Operations(string deployment, int limit = 10) => As<List<Operation>>(Send(HttpMethod.Get, $"/deployments/{deployment}/operations?limit={limit}")["operations"], "operations");
    public OperationEvents Events(string operation, long after, int wait, CancellationToken ct = default) =>
        Send<OperationEvents>(HttpMethod.Get, $"/operations/{operation}/events?after={after}&wait={wait}", ct: ct);

    /// <summary>The operation a 409 answer names, from wherever the API put it; null when it names none.</summary>
    public static Operation? RunningOperationOf(CloudApiError e)
    {
        if (e.Status != 409) return null;
        foreach (var node in new[] { e.Body?["operation"], e.Details?["operation"], e.Body?["error"]?["operation"] })
            if (node is JsonObject) return node.Deserialize<Operation>(Json);
        string? id = e.Details?["operationId"]?.ToString();
        return id is { Length: > 0 } ? new Operation(id, null, "rollout", "running", null, null, null, null, null, null) : null;
    }

    // --- status and logs ----------------------------------------------------------------------------------------------

    public sealed record OrchestratorStatus(string? State, string? Address, string? PublicIp, double? HeartbeatAgeSeconds, string? Version);
    public sealed record Counts(int? Active, int? Joining, int? Reconnecting);
    public sealed record Rate(double? In, double? Out, double? Workers);
    public sealed record GatewayStatus(string Id, string? Incarnation, string? Address, string? PrivateAddress, string? State, bool? InLoadBalancer, Counts? Clients, Rate? PacketsPerSecond, Rate? BytesPerSecond, double? Cpu, long? MemoryBytes, double? LoopLagMs, int? WorkerConnections, double? HeartbeatAgeSeconds);
    public sealed record WorkerStatus(string Id, int? Index, string? Size, string? State, string? PrivateAddress, double? TickMs, double? Utilization, int? Entities, int? Players, int? Bots, double? HeartbeatAgeSeconds);
    public sealed record MeshStatus(int? DesiredWorkers, int? LiveWorkers, int? Players, int? Bots, int? PendingJoins, int? Npcs, JsonElement? Scale);
    public sealed record LoadBalancerStatus(string? State, string? Ip, int? HealthyGateways);
    public sealed record DeploymentStatus(Deployment Deployment, Release? Release, string? Health, OrchestratorStatus? Orchestrator, List<GatewayStatus>? Gateways, List<WorkerStatus>? Workers, MeshStatus? Mesh, LoadBalancerStatus? LoadBalancer, string? SampledAt);

    public (DeploymentStatus Status, JsonNode Raw) GetStatus(string deployment)
    {
        var raw = Send(HttpMethod.Get, $"/deployments/{deployment}/status");
        return (As<DeploymentStatus>(raw, "a status"), raw);
    }

    public sealed record LogLine(string? At, string? Role, string? Instance, string? Level, string? Message);
    public sealed record LogPage(List<LogLine>? Lines, string? Next);

    public LogPage Logs(string deployment, string role, string? instance, DateTimeOffset? since, int limit)
    {
        var q = new StringBuilder($"/deployments/{deployment}/logs?role={Uri.EscapeDataString(role)}&limit={limit}");
        if (instance != null) q.Append("&instance=").Append(Uri.EscapeDataString(instance));
        if (since != null) q.Append("&since=").Append(Uri.EscapeDataString(since.Value.UtcDateTime.ToString("o")));
        return Send<LogPage>(HttpMethod.Get, q.ToString());
    }

    /// <summary>Read the server-sent event stream until it ends or <paramref name="ct"/> cancels; one JSON object per `data:` line.</summary>
    public void StreamLogs(string deployment, string role, string? instance, Action<LogLine> onLine, CancellationToken ct)
    {
        RefreshIfExpiring();
        string path = $"/deployments/{deployment}/logs/stream?role={Uri.EscapeDataString(role)}" + (instance != null ? "&instance=" + Uri.EscapeDataString(instance) : "");
        using var req = Build(HttpMethod.Get, path, null, null, true);
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("text/event-stream");
        using var resp = _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
        if ((int)resp.StatusCode >= 400)
        {
            string text = resp.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            JsonNode? node = null;
            try { node = JsonNode.Parse(text); } catch (JsonException) { }
            throw ToError(new RawResponse((int)resp.StatusCode, node, text), path);
        }
        using var stream = resp.Content.ReadAsStreamAsync(ct).GetAwaiter().GetResult();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = reader.ReadLineAsync(ct).AsTask().GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { break; }
            if (line == null) break;
            if (!line.StartsWith("data:")) continue;
            string data = line.Substring(5).Trim();
            if (data.Length == 0) continue;
            LogLine? entry = null;
            try { entry = JsonSerializer.Deserialize<LogLine>(data, Json); } catch (JsonException) { }
            if (entry != null) onLine(entry);
        }
    }
}
