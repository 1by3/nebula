using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nebula.Cli.Cloud;
using Nebula.Cli.Core;
using NUnit.Framework;

namespace Nebula.Cli.Tests;

internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Ui decides on colour once, from NO_COLOR; keep the captured output free of escape codes.
        Environment.SetEnvironmentVariable("NO_COLOR", "1");
        CloudApi.SlowDownStep = TimeSpan.Zero;
        OperationFollower.PollSeconds = 1;
    }
}

/// <summary>Runs the CLI in-process with captured output, an isolated config home, and the fake Cloud API.</summary>
internal static class Cli
{
    public sealed record Result(int Code, string Out, string Err)
    {
        public string All => Out + Err;
    }

    public static Result Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            int code = Program.Main(args);
            return new Result(code, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }
}

[TestFixture]
[NonParallelizable]
public class CloudCommandTests
{
    private FakeCloud _cloud = null!;
    private string _home = null!;
    private string _project = null!;

    [SetUp]
    public void SetUp()
    {
        _cloud = new FakeCloud();
        _home = Path.Combine(Path.GetTempPath(), "nebula-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("NEBULA_CLI_HOME", _home);
        Environment.SetEnvironmentVariable("NEBULA_CLOUD_API", _cloud.Url);
        Ui.AssumeInteractive = false;
        _project = MakeProject(cloud: true);
    }

    [TearDown]
    public void TearDown()
    {
        _cloud.Dispose();
        Environment.SetEnvironmentVariable("NEBULA_CLI_HOME", null);
        Environment.SetEnvironmentVariable("NEBULA_CLOUD_API", null);
        Ui.AssumeInteractive = false;
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_project, true); } catch { }
    }

    // --- helpers -----------------------------------------------------------------------------------------

    private void LoggedIn(string access = "acc1", string refresh = "ref1", DateTimeOffset? expires = null)
    {
        var config = new CliConfig
        {
            Cloud = new CliConfig.CloudSettings { ApiUrl = _cloud.Url, AccessToken = access, RefreshToken = refresh, ExpiresAt = expires ?? DateTimeOffset.UtcNow.AddHours(1) },
        };
        config.Save();
    }

    private CliConfig Config() => JsonSerializer.Deserialize<CliConfig>(File.ReadAllText(Path.Combine(_home, "config.json")), CliConfig.Json)!;

    private JsonNode ProjectFileJson() => JsonNode.Parse(File.ReadAllText(Path.Combine(_project, "nebula.json")))!;

    /// <summary>A folder that passes for a Unity project with Nebula: nebula.json, a Linux build tarball and manifest, the package source.</summary>
    private string MakeProject(bool cloud)
    {
        string root = Path.Combine(Path.GetTempPath(), "nebula-cli-tests", "proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.1f1\n");
        Directory.CreateDirectory(Path.Combine(root, "Packages", "com.1by3.nebula", "Runtime", "Protocol"));
        File.WriteAllText(Path.Combine(root, "Packages", "com.1by3.nebula", "Runtime", "Protocol", "Messages.cs"), "public struct HelloMsg { public const ushort ProtocolVersion = 12; }\n");
        File.WriteAllText(Path.Combine(root, "Packages", "manifest.json"), "{ \"dependencies\": {} }");
        Directory.CreateDirectory(Path.Combine(root, "Builds", "Linux64"));
        File.WriteAllText(Path.Combine(root, "Builds", "Linux64", "nebula-services.json"), "{\"containers\":[{\"id\":1}],\"settings\":{\"npcs\":\"0\"}}");
        var tarball = new byte[300 * 1024 + 17];
        RandomNumberGenerator.Fill(tarball);
        File.WriteAllBytes(Path.Combine(root, "Builds", "nebula-linux.tar.gz"), tarball);
        string cloudSection = cloud ? ",\n  \"cloud\": { \"organization\": \"acme\", \"project\": \"my-game\", \"deployment\": \"production\" }" : "";
        File.WriteAllText(Path.Combine(root, "nebula.json"), "{\n  \"nebula\": \"0.1.0\",\n  \"executable\": \"Nebula\",\n  \"custom\": { \"keep\": true },\n" +
            "  \"mesh\": { \"workers\": 2, \"dashboardPort\": " + _cloud.Port + ", \"gatewayPort\": 7000 },\n" +
            "  \"deploy\": { \"target\": \"" + (cloud ? "cloud" : "hetzner") + "\", \"workers\": 3, \"minWorkers\": 1, \"maxWorkers\": 5 }" + cloudSection + "\n}\n");
        return root;
    }

    private static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    // --- login / logout / account ------------------------------------------------------------------------------

    [Test]
    public void Login_polls_until_approved_and_stores_the_session()
    {
        _cloud.DeviceAnswers = new Queue<string>(new[] { "authorization_pending", "slow_down", "authorization_pending", "ok" });
        var r = Cli.Run("cloud", "login", "--no-browser", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        Assert.That(r.Out, Does.Contain("WXYZ-1234"));
        Assert.That(r.Out, Does.Contain(_cloud.Url + "/device?code=WXYZ-1234"));
        Assert.That(r.Out, Does.Contain("logged in as dev@example.com"));
        Assert.That(_cloud.Of("POST", "/v1/auth/device/token").Count(), Is.EqualTo(4));
        Assert.That(_cloud.Of("POST", "/v1/auth/device").First().Json!["client"]!.ToString(), Does.StartWith("nebula-cli/"));
        Assert.That(_cloud.Requests[0].Header("User-Agent"), Does.StartWith("nebula-cli/"));

        var c = Config();
        Assert.That(c.Cloud!.AccessToken, Is.EqualTo("acc1"));
        Assert.That(c.Cloud.RefreshToken, Is.EqualTo("ref1"));
        Assert.That(c.Cloud.ApiUrl, Is.EqualTo(_cloud.Url));
        Assert.That(c.Cloud.User!.Email, Is.EqualTo("dev@example.com"));
        Assert.That(c.Cloud.ExpiresAt, Is.GreaterThan(DateTimeOffset.UtcNow.AddMinutes(50)));
    }

    [Test]
    public void Login_reports_a_denied_code()
    {
        _cloud.DeviceAnswers = new Queue<string>(new[] { "access_denied" });
        var r = Cli.Run("cloud", "login", "--no-browser");
        Assert.That(r.Code, Is.EqualTo(1));
        Assert.That(r.Err, Does.Contain("denied"));
        Assert.That(File.Exists(Path.Combine(_home, "config.json")), Is.False);
    }

    [Test]
    public void Commands_without_credentials_say_to_log_in()
    {
        var r = Cli.Run("deployments");
        Assert.That(r.Code, Is.EqualTo(3));
        Assert.That(r.Err, Does.Contain("nebula cloud login"));
        Assert.That(_cloud.Requests, Is.Empty);
    }

    [Test]
    public void Expired_access_token_is_refreshed_and_the_rotation_saved()
    {
        LoggedIn(access: "stale", refresh: "ref1");
        _cloud.NextAccessToken = "acc2";
        _cloud.NextRefreshToken = "ref2";
        var r = Cli.Run("cloud", "account");
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        Assert.That(r.Out, Does.Contain("dev@example.com"));
        Assert.That(r.Out, Does.Contain("acme"));
        Assert.That(r.Out, Does.Contain("owner"));
        var me = _cloud.Of("GET", "/v1/me").ToList();
        Assert.That(me.Select(x => x.Header("Authorization")), Is.EqualTo(new[] { "Bearer stale", "Bearer acc2" }));
        Assert.That(_cloud.Of("POST", "/v1/auth/refresh").Single().Json!["refreshToken"]!.ToString(), Is.EqualTo("ref1"));
        var c = Config();
        Assert.That(c.Cloud!.AccessToken, Is.EqualTo("acc2"));
        Assert.That(c.Cloud.RefreshToken, Is.EqualTo("ref2"));
    }

    [Test]
    public void Refresh_failure_asks_to_log_in_again()
    {
        LoggedIn(access: "stale", refresh: "revoked");
        var r = Cli.Run("cloud", "account");
        Assert.That(r.Code, Is.EqualTo(3));
        Assert.That(r.Err, Does.Contain("nebula cloud login"));
    }

    [Test]
    public void Logout_revokes_and_clears()
    {
        LoggedIn();
        var r = Cli.Run("cloud", "logout");
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        Assert.That(_cloud.Of("POST", "/v1/auth/logout").Single().Json!["refreshToken"]!.ToString(), Is.EqualTo("ref1"));
        Assert.That(Config().Cloud!.IsLoggedIn, Is.False);
    }

    // --- deploy ------------------------------------------------------------------------------------------------

    [Test]
    public void Deploy_uploads_the_build_creates_a_release_and_follows_the_rollout()
    {
        LoggedIn();
        var r = Cli.Run("deploy", "--skip-build", "--label", "v2", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);

        string tarball = Path.Combine(_project, "Builds", "nebula-linux.tar.gz");
        var artifact = _cloud.Of("POST", "/v1/projects/prj_1/artifacts").Single().Json!;
        Assert.That(artifact["sha256"]!.ToString(), Is.EqualTo(Sha(tarball)));
        Assert.That((long)artifact["sizeBytes"]!, Is.EqualTo(new FileInfo(tarball).Length));
        Assert.That(artifact["kind"]!.ToString(), Is.EqualTo("linux-server"));
        Assert.That(_cloud.Uploads["art_1"], Is.EqualTo(File.ReadAllBytes(tarball)));
        Assert.That(_cloud.Of("PUT", "/upload/art_1").Single().Header("Content-Type"), Is.EqualTo("application/gzip"));
        Assert.That(_cloud.Of("POST", "/v1/projects/prj_1/artifacts/art_1/complete").Count(), Is.EqualTo(1));

        var release = _cloud.Of("POST", "/v1/projects/prj_1/releases").Single().Json!;
        Assert.That(release["artifactId"]!.ToString(), Is.EqualTo("art_1"));
        Assert.That(release["label"]!.ToString(), Is.EqualTo("v2"));
        Assert.That((int)release["protocolVersion"]!, Is.EqualTo(12));
        Assert.That(release["nebulaVersion"]!.ToString(), Is.EqualTo(Platform.CliVersion));
        Assert.That(release["manifest"]!["containers"]!.AsArray().Count, Is.EqualTo(1));

        var rollout = _cloud.Of("POST", "/v1/deployments/dep_1/rollouts").Single();
        Assert.That(rollout.Json!["releaseId"]!.ToString(), Is.EqualTo("rel_2"));
        Assert.That((bool)rollout.Json["allowProtocolChange"]!, Is.False);
        Assert.That(rollout.Header("Idempotency-Key"), Does.StartWith(CloudApi.InvocationId + "-rollout-rel_2"));
        Assert.That(_cloud.Of("GET", "/v1/operations/op_1/events").Count(), Is.EqualTo(2));

        Assert.That(r.Out, Does.Contain("step upload-build"));
        Assert.That(r.Out, Does.Contain("rollout complete"));
        Assert.That(r.Out, Does.Contain("rollout succeeded"));
        Assert.That(r.Out, Does.Contain("gateway    203.0.113.10:7000"));
        Assert.That(r.Out, Does.Contain("web        https://203-0-113-10.example.test/"));
        Assert.That(r.Out, Does.Contain("dashboard  https://cloud.example.test/d/dep_1/mesh"));
    }

    [Test]
    public void Deploy_reports_a_failed_rollout()
    {
        LoggedIn();
        _cloud.Outcome = "failed";
        var r = Cli.Run("deploy", "--skip-build", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(1));
        Assert.That(r.Out, Does.Contain("orchestrator did not answer"));
        Assert.That(r.Err, Does.Contain("rollout op_1 failed at wait-ready: the orchestrator never became ready"));
        Assert.That(r.Err, Does.Contain("nebula logs --cloud orchestrator"));
    }

    [Test]
    public void Deploy_skips_the_upload_when_the_artifact_exists_and_rolls_out_an_existing_release()
    {
        LoggedIn();
        var r = Cli.Run("deploy", "--release", "rel_1", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        Assert.That(_cloud.Of("POST", "/v1/projects/prj_1/artifacts"), Is.Empty);
        Assert.That(_cloud.Of("POST", "/v1/deployments/dep_1/rollouts").Single().Json!["releaseId"]!.ToString(), Is.EqualTo("rel_1"));
    }

    [Test]
    public void Deploy_patches_the_worker_band_before_the_rollout()
    {
        LoggedIn();
        var r = Cli.Run("deploy", "--release", "rel_1", "--min", "0", "--max", "8", "--allow-protocol-change", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        var patch = _cloud.Of("PATCH", "/v1/deployments/dep_1").Single().Json!;
        Assert.That((int)patch["minWorkers"]!, Is.EqualTo(0));
        Assert.That((int)patch["maxWorkers"]!, Is.EqualTo(8));
        Assert.That((bool)_cloud.Of("POST", "/v1/deployments/dep_1/rollouts").Single().Json!["allowProtocolChange"]!, Is.True);
        int patchAt = _cloud.Requests.FindIndex(x => x.Method == "PATCH");
        int rolloutAt = _cloud.Requests.FindIndex(x => x.Path.EndsWith("/rollouts"));
        Assert.That(patchAt, Is.LessThan(rolloutAt));
    }

    [Test]
    public void Deploy_attaches_to_the_operation_a_409_names()
    {
        LoggedIn();
        _cloud.ConflictOperation = FakeCloud.J(new { id = "op_9", deploymentId = "dep_1", kind = "rollout", state = "running", steps = new[] { new { name = "restart-orchestrator", state = "running" } } });
        var r = Cli.Run("deploy", "--release", "rel_1", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        Assert.That(r.Out, Does.Contain("op_9"));
        Assert.That(r.Out, Does.Contain("already running"));
        Assert.That(_cloud.Of("GET", "/v1/operations/op_9/events").Count(), Is.GreaterThanOrEqualTo(1));
        Assert.That(_cloud.Of("POST", "/v1/deployments/dep_1/rollouts").Count(), Is.EqualTo(1));
    }

    [Test]
    public void Deploy_reattaches_to_a_running_operation_instead_of_starting_another()
    {
        LoggedIn();
        _cloud.Operations.Add(FakeCloud.J(new { id = "op_5", deploymentId = "dep_1", kind = "rollout", state = "running", steps = new[] { new { name = "wait-ready", state = "running" } } }));
        var r = Cli.Run("deploy", "--skip-build", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        Assert.That(r.Out, Does.Contain("op_5"));
        Assert.That(_cloud.Of("POST", "/v1/projects/prj_1/artifacts"), Is.Empty);
        Assert.That(_cloud.Of("POST", "/v1/deployments/dep_1/rollouts"), Is.Empty);
    }

    [Test]
    public void Follow_says_how_to_reattach_when_cancelled()
    {
        LoggedIn();
        _cloud.Operations.Add(FakeCloud.J(new { id = "op_7", deploymentId = "dep_1", kind = "rollout", state = "running", steps = new object[0] }));
        var api = CloudApi.Require(new Context());
        var op = api.GetOperation("op_7");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stdout = new StringWriter();
        var oldOut = Console.Out;
        Console.SetOut(stdout);
        try
        {
            Assert.Throws<OperationCanceledException>(() => OperationFollower.Follow(api, op, cts.Token));
        }
        finally { Console.SetOut(oldOut); }
        Assert.That(stdout.ToString(), Does.Contain("op_7 (rollout) is still running; re-run the command to reattach"));
    }

    [Test]
    public void First_cloud_deploy_records_the_target_in_nebula_json_and_keeps_other_keys()
    {
        LoggedIn();
        string project = MakeProject(cloud: false);
        try
        {
            var r = Cli.Run("deploy", "--target", "cloud", "--release", "rel_1", "--yes", "--project", project);
            Assert.That(r.Code, Is.EqualTo(0), r.All);
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(project, "nebula.json")))!;
            Assert.That(json["cloud"]!["organization"]!.ToString(), Is.EqualTo("acme"));
            Assert.That(json["cloud"]!["project"]!.ToString(), Is.EqualTo("my-game"));
            Assert.That(json["cloud"]!["deployment"]!.ToString(), Is.EqualTo("production"));
            Assert.That(json["deploy"]!["target"]!.ToString(), Is.EqualTo("cloud"));
            Assert.That((int)json["deploy"]!["maxWorkers"]!, Is.EqualTo(5));
            Assert.That((bool)json["custom"]!["keep"]!, Is.True);
            Assert.That((int)json["mesh"]!["dashboardPort"]!, Is.EqualTo(_cloud.Port));
            Assert.That(r.Out, Does.Contain("nebula.json: cloud = acme/my-game/production"));
        }
        finally { Directory.Delete(project, true); }
    }

    [Test]
    public void First_cloud_deploy_creates_a_missing_project_and_deployment_with_defaults()
    {
        LoggedIn();
        _cloud.Projects.Clear();
        _cloud.Deployments.Clear();
        string project = MakeProject(cloud: false);
        try
        {
            var r = Cli.Run("deploy", "--target", "cloud", "--release", "rel_1", "--yes", "--region", "nyc3", "--worker-size", "medium", "--project", project);
            Assert.That(r.Code, Is.EqualTo(0), r.All);
            var created = _cloud.Of("POST", "/v1/orgs/org_1/projects").Single().Json!;
            Assert.That(created["slug"]!.ToString(), Does.StartWith("proj-"));
            var dep = _cloud.Of("POST", "/v1/projects/prj_1/deployments").Single().Json!;
            Assert.That(dep["name"]!.ToString(), Is.EqualTo("production"));
            Assert.That(dep["region"]!.ToString(), Is.EqualTo("nyc3"));
            Assert.That(dep["workerSize"]!.ToString(), Is.EqualTo("medium"));
            Assert.That((int)dep["minWorkers"]!, Is.EqualTo(1));
            Assert.That((int)dep["maxWorkers"]!, Is.EqualTo(5));
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(project, "nebula.json")))!;
            Assert.That(json["cloud"]!["deployment"]!.ToString(), Is.EqualTo("production"));
        }
        finally { Directory.Delete(project, true); }
    }

    // --- deployments / status / logs -------------------------------------------------------------------------------

    [Test]
    public void Deployments_lists_the_project_organization()
    {
        LoggedIn();
        var r = Cli.Run("deployments", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        Assert.That(r.Out, Does.Contain("deployment  project  organization  state    region  workers     release  gateway            id"));
        Assert.That(r.Out, Does.Contain("production  my-game  acme          running  nyc3    small 1..4  rel_1    203.0.113.10:7000  dep_1"));
        var json = Cli.Run("deployments", "--json", "--project", _project);
        Assert.That(JsonNode.Parse(json.Out)!.AsArray()[0]!["deployment"]!["id"]!.ToString(), Is.EqualTo("dep_1"));
    }

    [Test]
    public void Status_renders_the_deployment()
    {
        LoggedIn();
        _cloud.Status = FakeCloud.J(new
        {
            release = _cloud.Releases[0],
            health = "healthy",
            orchestrator = new { state = "running", address = "10.10.0.5", publicIp = "203.0.113.9", heartbeatAgeSeconds = 1.3, version = "0.1.0-alpha.22" },
            gateways = new[]
            {
                new { id = "gw1", incarnation = 3, address = "203.0.113.10:7000", privateAddress = "10.10.0.6", state = "ready", inLoadBalancer = true,
                      clients = new { active = 12, joining = 1, reconnecting = 0 }, packetsPerSecond = new { @in = 640.0, @out = 1900.0 },
                      bytesPerSecond = new { @in = 51200.0, @out = 2097152.0, workers = 4096.0 }, cpu = 0.31, memoryBytes = 123456789L, loopLagMs = 0.8, workerConnections = 2, heartbeatAgeSeconds = 0.5 },
                new { id = "gw2", incarnation = 1, address = "203.0.113.11:7000", privateAddress = "10.10.0.7", state = "draining", inLoadBalancer = false,
                      clients = new { active = 2, joining = 0, reconnecting = 1 }, packetsPerSecond = new { @in = 10.0, @out = 20.0 },
                      bytesPerSecond = new { @in = 100.0, @out = 200.0, workers = 50.0 }, cpu = 0.05, memoryBytes = 1000L, loopLagMs = 0.1, workerConnections = 2, heartbeatAgeSeconds = 2.0 },
            },
            workers = new object[]
            {
                new { id = "w1", index = 1, size = "small", state = "running", privateAddress = "10.10.0.20", tickMs = 3.2, utilization = 0.45, entities = 812, players = 9, bots = 0, heartbeatAgeSeconds = 0.9 },
                new { id = "w2", index = 2, size = "small", state = "launching", players = 0, bots = 0 },
            },
            mesh = new { desiredWorkers = 2, liveWorkers = 1, players = 14, bots = 0, pendingJoins = 1, npcs = 50, scale = new { minWorkers = 1, maxWorkers = 4 } },
            loadBalancer = new { state = "active", ip = "203.0.113.10", healthyGateways = 1 },
            sampledAt = "2026-09-16T10:00:00Z",
        });
        var r = Cli.Run("status", "--cloud", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        const string expected = @"
    deployment production (dep_1)  state=running health=healthy region=nyc3 workers=small 1..4 gateways=standard 1..3
    release    #1 rel_1 v1  nebula 0.1.0 protocol 12
    gateway    203.0.113.10:7000   (client: Nebula -nebula-role client -nebula-gateway 203.0.113.10:7000)
    web        https://203-0-113-10.example.test/
    dashboard  https://cloud.example.test/d/dep_1/mesh
    orchestrator running  address 10.10.0.5 public 203.0.113.9  version 0.1.0-alpha.22  heartbeat 1.3
    load balancer active  ip 203.0.113.10  healthy gateways 1
    mesh       desired 2 live 1 worker(s), 14 player(s), 0 bot(s), 1 pending join(s), 50 NPC(s)
    sampled    2026-09-16T10:00:00Z

    gateways:
    gateway  state     lb   address            clients  joining  reconn  pps in/out  bytes/s in/out  to workers  cpu  lag ms  workers  hb s
    gw1#3    ready     yes  203.0.113.10:7000  12       1        0       640/1900    50.0K/2.0M      4.0K        31%  0.8     2        0.5
    gw2#1    draining  no   203.0.113.11:7000  2        0        1       10/20       100/200         50          5%   0.1     2        2.0

    workers:
    worker  index  size   state      address     tick ms  util  entities  players  bots  hb s
    w1      1      small  running    10.10.0.20  3.2      45%   812       9        0     0.9
    w2      2      small  launching                                       0        0
";
        Assert.That(r.Out, Is.EqualTo(expected.Replace("\r\n", "\n")));
    }

    [Test]
    public void Status_json_prints_the_raw_document()
    {
        LoggedIn();
        _cloud.Status = FakeCloud.J(new { health = "degraded" });
        var r = Cli.Run("status", "--cloud", "--json", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        Assert.That(JsonNode.Parse(r.Out)!["health"]!.ToString(), Is.EqualTo("degraded"));
    }

    [Test]
    public void Logs_reads_a_page_then_follows_the_event_stream()
    {
        LoggedIn();
        _cloud.LogLines = new List<JsonObject>
        {
            FakeCloud.J(new { at = "2026-09-16T10:00:00Z", role = "worker", instance = "w1", level = "info", message = "worker registered" }),
            FakeCloud.J(new { at = "2026-09-16T10:00:01Z", role = "worker", instance = "w1", level = "warn", message = "tick 20 ms" }),
        };
        _cloud.StreamLines = new List<JsonObject>
        {
            FakeCloud.J(new { at = "2026-09-16T10:00:01Z", role = "worker", instance = "w1", level = "warn", message = "tick 20 ms" }),
            FakeCloud.J(new { at = "2026-09-16T10:00:02Z", role = "worker", instance = "w1", level = "info", message = "container 1 leased" }),
            FakeCloud.J(new { at = "2026-09-16T10:00:03Z", role = "worker", instance = "w1", level = "error", message = "handover timed out" }),
        };
        var r = Cli.Run("logs", "--cloud", "w1", "--follow", "--since", "10m", "-n", "2", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        var page = _cloud.Of("GET", "/v1/deployments/dep_1/logs").Single();
        var stream = _cloud.Of("GET", "/v1/deployments/dep_1/logs/stream").Single();
        Assert.That(stream.Header("Accept"), Is.EqualTo("text/event-stream"));
        Assert.That(page.Param("role"), Is.EqualTo("worker"));
        Assert.That(page.Param("instance"), Is.EqualTo("w1"));
        Assert.That(page.Param("limit"), Is.EqualTo("2"));
        Assert.That(DateTimeOffset.Parse(page.Param("since")!), Is.EqualTo(DateTimeOffset.UtcNow.AddMinutes(-10)).Within(TimeSpan.FromMinutes(1)));
        Assert.That(stream.Param("instance"), Is.EqualTo("w1"));
        Assert.That(r.Out, Is.EqualTo(
            "2026-09-16 10:00:00 w1           INFO  worker registered\n" +
            "2026-09-16 10:00:01 w1           WARN  tick 20 ms\n" +
            "2026-09-16 10:00:02 w1           INFO  container 1 leased\n" +
            "2026-09-16 10:00:03 w1           ERROR handover timed out\n"));
    }

    [Test]
    public void Logs_maps_instance_names_to_roles_and_passes_since()
    {
        LoggedIn();
        var r = Cli.Run("logs", "--cloud", "gw2", "--since", "2026-09-16T09:00:00Z", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        var req = _cloud.Of("GET", "/v1/deployments/dep_1/logs").Single();
        Assert.That(req.Param("role"), Is.EqualTo("gateway"));
        Assert.That(req.Param("instance"), Is.EqualTo("gw2"));
        Assert.That(req.Param("since"), Does.StartWith("2026-09-16T09:00:00"));
        Assert.That(req.Param("limit"), Is.EqualTo("60"));

        var worker = Cli.Run("logs", "--cloud", "w3", "--project", _project);
        Assert.That(worker.Code, Is.EqualTo(0), worker.All);
        var w = _cloud.Of("GET", "/v1/deployments/dep_1/logs").Last();
        Assert.That(w.Param("role"), Is.EqualTo("worker"));
        Assert.That(w.Param("instance"), Is.EqualTo("w3"));
        Assert.That(worker.Out, Is.EqualTo(""));
    }

    // --- scale / rollback / destroy ----------------------------------------------------------------------------------

    [Test]
    public void Scale_posts_the_bands_to_the_cloud_deployment()
    {
        LoggedIn();
        var r = Cli.Run("scale", "--min", "0", "--max", "6", "--gateways-max", "2", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        var body = _cloud.Of("POST", "/v1/deployments/dep_1/scale").Single().Json!;
        Assert.That((int)body["minWorkers"]!, Is.EqualTo(0));
        Assert.That((int)body["maxWorkers"]!, Is.EqualTo(6));
        Assert.That(body["minGateways"], Is.Null);
        Assert.That((int)body["maxGateways"]!, Is.EqualTo(2));
        Assert.That(r.Out, Does.Contain("workers 0..6, gateways 1..2"));
    }

    [Test]
    public void Scale_sets_the_local_orchestrator_band_through_its_dashboard()
    {
        var r = Cli.Run("scale", "--target", "local", "--min", "2", "--max", "3", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        var body = _cloud.Of("POST", "/api/scale/limits").Single().Json!;
        Assert.That((int)body["min"]!, Is.EqualTo(2));
        Assert.That((int)body["max"]!, Is.EqualTo(3));
        Assert.That(r.Out, Does.Contain("local mesh: workers 2..3"));
        Assert.That(_cloud.Requests.Any(x => x.Path.StartsWith("/v1/")), Is.False);
    }

    [Test]
    public void Rollback_rolls_to_the_previous_release_and_follows()
    {
        LoggedIn();
        var r = Cli.Run("rollback", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        var body = _cloud.Of("POST", "/v1/deployments/dep_1/rollback").Single().Json;
        Assert.That(body?["releaseId"], Is.Null);
        Assert.That(r.Out, Does.Contain("rollback succeeded"));
        Assert.That(r.Out, Does.Contain("on release rel_0"));
        var explicitRelease = Cli.Run("rollback", "--release", "rel_1", "--project", _project);
        Assert.That(explicitRelease.Code, Is.EqualTo(0), explicitRelease.All);
        Assert.That(_cloud.Of("POST", "/v1/deployments/dep_1/rollback").Last().Json!["releaseId"]!.ToString(), Is.EqualTo("rel_1"));
    }

    [Test]
    public void Destroy_needs_the_deployment_name_or_yes()
    {
        LoggedIn();
        var refused = Cli.Run("destroy", "--project", _project);
        Assert.That(refused.Code, Is.Not.EqualTo(0));
        Assert.That(_cloud.Of("DELETE", "/v1/deployments/dep_1"), Is.Empty);

        Ui.AssumeInteractive = true;
        Console.SetIn(new StringReader("staging\n"));
        var wrong = Cli.Run("destroy", "--project", _project);
        Assert.That(wrong.Code, Is.EqualTo(1));
        Assert.That(wrong.Err, Does.Contain("did not match"));
        Assert.That(_cloud.Of("DELETE", "/v1/deployments/dep_1"), Is.Empty);

        Console.SetIn(new StringReader("production\n"));
        var typed = Cli.Run("destroy", "--project", _project);
        Assert.That(typed.Code, Is.EqualTo(0), typed.All);
        Assert.That(_cloud.Of("DELETE", "/v1/deployments/dep_1").Count(), Is.EqualTo(1));
        Assert.That(typed.Out, Does.Contain("destroy succeeded"));
        Assert.That(typed.Out, Does.Contain("acme/my-game/production destroyed"));
    }

    [Test]
    public void Destroy_with_yes_skips_the_prompt()
    {
        LoggedIn();
        var r = Cli.Run("destroy", "--yes", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        Assert.That(_cloud.Of("DELETE", "/v1/deployments/dep_1").Single().Header("Idempotency-Key"), Does.EndWith("-destroy"));
        Assert.That(_cloud.Deployments[0]["state"]!.ToString(), Is.EqualTo("destroyed"));
    }

    [Test]
    public void Dashboard_prints_the_cloud_url()
    {
        LoggedIn();
        var r = Cli.Run("dashboard", "--no-open", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        Assert.That(r.Out, Does.Contain("https://cloud.example.test/d/dep_1/mesh"));
        var local = Cli.Run("dashboard", "--no-open", "--target", "local", "--project", _project);
        Assert.That(local.Out, Does.Contain($"http://127.0.0.1:{_cloud.Port}/"));
    }

    [Test]
    public void Api_errors_carry_hints()
    {
        LoggedIn();
        _cloud.Deployments[0]["id"] = "dep_1";
        _cloud.Projects.Clear();
        var r = Cli.Run("status", "--cloud", "--project", _project);
        Assert.That(r.Code, Is.EqualTo(1));
        Assert.That(r.Err, Does.Contain("no project 'my-game'"));
    }
}
