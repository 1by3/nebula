using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Nebula;
using Nebula.Hosting;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// <see cref="CloudWorkerHost"/> against a fake deployment-scoped resource API: the orchestrator's requests, the
/// states it maps, and what it does when the API says no.
/// </summary>
[TestFixture]
public class CloudWorkerHostTests
{
    /// <summary>The resource API as the host sees it: an in-memory worker table behind HttpListener.</summary>
    private sealed class FakeResourceApi : IDisposable
    {
        public readonly string Url;
        public readonly string Token = "nbd_test";
        public readonly List<string> Requests = new();
        public readonly Dictionary<string, Dictionary<string, object?>> Workers = new();
        public int CreateStatus = 201;
        public bool SpendLimited;
        public bool UnparkGone;
        private readonly HttpListener _listener = new();
        private readonly object _gate = new();

        public FakeResourceApi()
        {
            int port = FreePort();
            Url = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _listener.BeginGetContext(OnContext, null);
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port;
        }

        public Dictionary<string, object?> Seed(string workerId, string state, string address = "10.0.0.9")
        {
            lock (_gate)
            {
                var w = new Dictionary<string, object?> { ["workerId"] = workerId, ["resourceId"] = "do:droplet:" + workerId, ["index"] = 1, ["state"] = state, ["privateAddress"] = address, ["size"] = "small" };
                Workers[workerId] = w;
                return w;
            }
        }

        public void SetState(string workerId, string state, string? address = null)
        {
            lock (_gate)
            {
                Workers[workerId]["state"] = state;
                if (address != null) Workers[workerId]["privateAddress"] = address;
            }
        }

        private void OnContext(IAsyncResult ar)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.EndGetContext(ar); } catch { return; }
            try { _listener.BeginGetContext(OnContext, null); } catch { }
            try { Handle(ctx); } catch (Exception e) { Console.Error.WriteLine(e); }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            string path = req.Url!.AbsolutePath;
            string body;
            using (var r = new StreamReader(req.InputStream)) body = r.ReadToEnd();
            lock (_gate) Requests.Add(req.HttpMethod + " " + path + (body.Length > 0 ? " " + body : ""));
            if (req.Headers["Authorization"] != "Bearer " + Token) { Answer(ctx, 401, "{\"error\":{\"code\":\"unauthorized\",\"message\":\"bad token\"}}"); return; }
            lock (_gate)
            {
                if (path == "/v1/d/self") { Answer(ctx, 200, JsonSerializer.Serialize(new { deploymentId = "dep_1", region = "nyc3", workerSize = "small", maxWorkers = 4, spendLimited = SpendLimited })); return; }
                if (path == "/v1/d/workers" && req.HttpMethod == "GET") { Answer(ctx, 200, JsonSerializer.Serialize(new { workers = Workers.Values })); return; }
                if (path == "/v1/d/workers" && req.HttpMethod == "POST")
                {
                    if (SpendLimited) { Answer(ctx, 402, "{\"error\":{\"code\":\"spend_limit\",\"message\":\"the organization reached its spend limit\"}}"); return; }
                    if (CreateStatus != 201) { Answer(ctx, CreateStatus, "{\"error\":{\"code\":\"conflict\",\"message\":\"at the worker ceiling\"}}"); return; }
                    var o = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body)!;
                    string id = o["workerId"].GetString()!;
                    var w = Seed(id, "launching", "");
                    w["index"] = o["index"].GetInt64();
                    Answer(ctx, 201, JsonSerializer.Serialize(new { worker = w }));
                    return;
                }
                string prefix = "/v1/d/workers/";
                if (path.StartsWith(prefix))
                {
                    string rest = path.Substring(prefix.Length);
                    string id = rest.Split('/')[0];
                    string action = rest.Contains('/') ? rest.Substring(rest.IndexOf('/') + 1) : "";
                    if (!Workers.TryGetValue(id, out var w)) { Answer(ctx, 404, "{\"error\":{\"code\":\"not_found\",\"message\":\"no such worker\"}}"); return; }
                    if (req.HttpMethod == "DELETE") { w["state"] = "exiting"; Answer(ctx, 202, JsonSerializer.Serialize(new { worker = w })); return; }
                    if (action == "park") { w["state"] = "parked"; w["parkedUntil"] = DateTime.UtcNow.AddMinutes(30).ToString("o"); Answer(ctx, 200, JsonSerializer.Serialize(new { worker = w })); return; }
                    if (action == "unpark")
                    {
                        if (UnparkGone) { Workers.Remove(id); Answer(ctx, 410, "{\"error\":{\"code\":\"gone\",\"message\":\"released\"}}"); return; }
                        w["state"] = "running"; Answer(ctx, 200, JsonSerializer.Serialize(new { worker = w })); return;
                    }
                    Answer(ctx, 200, JsonSerializer.Serialize(new { worker = w }));
                    return;
                }
            }
            Answer(ctx, 404, "{\"error\":{\"code\":\"not_found\",\"message\":\"nope\"}}");
        }

        private static void Answer(HttpListenerContext ctx, int status, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        public bool Saw(string prefix) { lock (_gate) return Requests.Any(r => r.StartsWith(prefix)); }
        public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
    }

    private static CloudWorkerHost NewHost(FakeResourceApi api, List<string> log) =>
        new CloudWorkerHost(new CloudWorkerHostSettings { ApiUrl = api.Url, DeploymentToken = api.Token, PollIntervalSeconds = 0.05f });

    private static bool Pump(CloudWorkerHost host, Func<bool> until, double seconds = 5)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            host.Tick();
            if (until()) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    private static void Ready(CloudWorkerHost host, List<string> log)
    {
        host.Initialize((level, message) => log.Add(level + ": " + message));
        Assert.That(Pump(host, () => host.IsReady || host.InitializationError.Length > 0), Is.True);
        Assert.That(host.InitializationError, Is.Empty, string.Join("\n", log));
    }

    [Test]
    public void InitializeSweepsLeftoversAndLaunchFollowsTheApiStates()
    {
        using var api = new FakeResourceApi();
        api.Seed("w9", "running");
        api.Seed("w8", "exited");
        var log = new List<string>();
        using var host = NewHost(api, log);
        Ready(host, log);
        Assert.That(Pump(host, () => api.Saw("DELETE /v1/d/workers/w9")), Is.True, "a running leftover is deleted");
        Assert.That(api.Saw("DELETE /v1/d/workers/w8"), Is.False, "an exited one is left alone");
        Assert.That(host.Name, Is.EqualTo("cloud"));
        Assert.That(host.SupportsParking, Is.True);

        var handle = host.Launch(new WorkerLaunchSpec { WorkerId = "w1", Index = 1, Port = 7101, CommonArgs = "-nebula-token t" });
        Assert.That(handle.State, Is.EqualTo(WorkerHandleState.Launching));
        Assert.That(Pump(host, () => handle.Describe.Contains("do:droplet:w1")), Is.True, "the create answer names the resource");
        Assert.That(api.Requests.Any(r => r.StartsWith("POST /v1/d/workers ") && r.Contains("\"workerId\":\"w1\"") && r.Contains("\"port\":7101") && r.Contains("-nebula-token t")), Is.True);
        Assert.That(handle.State, Is.EqualTo(WorkerHandleState.Launching));

        api.SetState("w1", "running", "10.0.0.5");
        Assert.That(Pump(host, () => handle.State == WorkerHandleState.Running), Is.True, "polling picks up the state change");
        Assert.That(handle.Address, Is.EqualTo("10.0.0.5"));

        host.Park(handle, 0);
        Assert.That(handle.State, Is.EqualTo(WorkerHandleState.Parked));
        Assert.That(Pump(host, () => api.Saw("POST /v1/d/workers/w1/park")), Is.True);
        Assert.That(Pump(host, () => handle.ParkedSecondsRemaining > 60), Is.True, "the API's parkedUntil is reported");
        Assert.That(host.Unpark(handle), Is.True);
        Assert.That(handle.State, Is.EqualTo(WorkerHandleState.Running));
        Assert.That(Pump(host, () => api.Saw("POST /v1/d/workers/w1/unpark")), Is.True);

        host.Kill(handle);
        Assert.That(handle.State, Is.EqualTo(WorkerHandleState.Exited));
        Assert.That(Pump(host, () => api.Saw("DELETE /v1/d/workers/w1")), Is.True);
    }

    [Test]
    public void ApiRefusalsBecomeFailedHandlesWithTheReason()
    {
        using var api = new FakeResourceApi();
        var log = new List<string>();
        using var host = NewHost(api, log);
        Ready(host, log);

        api.SpendLimited = true;
        var limited = host.Launch(new WorkerLaunchSpec { WorkerId = "w2", Index = 2, Port = 7102 });
        Assert.That(Pump(host, () => limited.State == WorkerHandleState.Failed), Is.True);
        Assert.That(limited.Reason, Does.Contain("spend limit"));

        api.SpendLimited = false; api.CreateStatus = 409;
        var ceiling = host.Launch(new WorkerLaunchSpec { WorkerId = "w3", Index = 3, Port = 7103 });
        Assert.That(Pump(host, () => ceiling.State == WorkerHandleState.Failed), Is.True);
        Assert.That(ceiling.Reason, Does.Contain("ceiling"));

        api.CreateStatus = 201;
        var w4 = host.Launch(new WorkerLaunchSpec { WorkerId = "w4", Index = 4, Port = 7104 });
        Assert.That(Pump(host, () => w4.Describe.Contains("do:droplet:w4")), Is.True);
        api.SetState("w4", "running", "10.0.0.4");
        Assert.That(Pump(host, () => w4.State == WorkerHandleState.Running), Is.True);
        host.Park(w4, 10);
        api.UnparkGone = true;
        Assert.That(host.Unpark(w4), Is.True, "optimistic: the machine never stopped");
        Assert.That(Pump(host, () => w4.State == WorkerHandleState.Exited), Is.True, "but a 410 turns it into an exit, so the orchestrator launches afresh");

        var w5 = host.Launch(new WorkerLaunchSpec { WorkerId = "w5", Index = 5, Port = 7105 });
        Assert.That(Pump(host, () => w5.Describe.Contains("do:droplet:w5")), Is.True);
        api.SetState("w5", "failed");
        Assert.That(Pump(host, () => w5.State == WorkerHandleState.Failed), Is.True, "a failed machine is reported as such");
    }

    [Test]
    public void ABadTokenOrMissingSettingsNeverReadies()
    {
        using var api = new FakeResourceApi();
        var log = new List<string>();
        using var wrong = new CloudWorkerHost(new CloudWorkerHostSettings { ApiUrl = api.Url, DeploymentToken = "nbd_wrong", PollIntervalSeconds = 0.05f });
        wrong.Initialize((l, m) => log.Add(m));
        Assert.That(Pump(wrong, () => wrong.InitializationError.Length > 0), Is.True);
        Assert.That(wrong.IsReady, Is.False);
        Assert.That(wrong.InitializationError, Does.Contain("refused"));
        var h = wrong.Launch(new WorkerLaunchSpec { WorkerId = "w1", Index = 1, Port = 1 });
        Assert.That(h.State, Is.EqualTo(WorkerHandleState.Failed));

        using var unset = new CloudWorkerHost(new CloudWorkerHostSettings());
        unset.Initialize(null);
        Assert.That(unset.InitializationError, Does.Contain("nebula-cloud-api"));
        Assert.That(CloudWorkerHost.MapState("bogus", WorkerHandleState.Parked), Is.EqualTo(WorkerHandleState.Parked));
    }
}
