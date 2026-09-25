using System.Text.Json;
using System.Text.Json.Nodes;
using Nebula.Hosting;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// What a worker host hands every worker it launches: the service manifest's Env map, then the file
/// NEBULA_ENV_FILE names, without reserved names, and never a value in a log line.
/// </summary>
[TestFixture]
[NonParallelizable]
public class WorkerEnvironmentTests
{
    private string _dir = "";
    private readonly List<string> _warnings = new();

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nebula-worker-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        WorkerEnvironment.ManifestEnv = null;
        _warnings.Clear();
        CommandLine.Override(new Dictionary<string, string>());
    }

    [TearDown]
    public void TearDown()
    {
        WorkerEnvironment.ManifestEnv = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static Func<string, string> Env(Dictionary<string, string> vars) => k => vars.TryGetValue(k, out var v) ? v : null;

    [Test]
    public void Manifest_env_round_trips_through_the_service_manifest()
    {
        var node = JsonSerializer.SerializeToNode(new ServiceManifest(), ServiceManifest.Json)!.AsObject();
        node["Env"] = new JsonObject { ["GAME_API_URL"] = "https://api.example.test", ["FEATURE_X"] = "on" };
        string path = Write("nebula-services.json", node.ToJsonString());
        var manifest = ServiceManifest.Load(path);
        Assert.That(manifest.Env, Is.Not.Null);
        Assert.That(manifest.Env["GAME_API_URL"], Is.EqualTo("https://api.example.test"));
        Assert.That(manifest.Env["FEATURE_X"], Is.EqualTo("on"));

        // A manifest Unity exported (no Env) still loads.
        string plain = Write("plain.json", JsonSerializer.Serialize(new ServiceManifest(), ServiceManifest.Json));
        Assert.That(ServiceManifest.Load(plain).Env, Is.Null);
    }

    [Test]
    public void Resolve_applies_the_manifest_then_the_env_file_and_drops_reserved_names()
    {
        WorkerEnvironment.ManifestEnv = new Dictionary<string, string> { ["GAME_API_URL"] = "https://manifest", ["FEATURE_X"] = "on", ["PORT"] = "1" };
        string secrets = Write("secrets.env", "GAME_API_URL=https://override\nGAME_API_KEY=\"s3cr3t value\"\nNEBULA_MESH_TOKEN=hunter2\n");
        var vars = WorkerEnvironment.Resolve(Env(new() { [NebulaEnv.EnvFileVariable] = secrets }), _warnings.Add);

        Assert.That(vars, Is.EquivalentTo(new Dictionary<string, string>
        {
            ["GAME_API_URL"] = "https://override",
            ["FEATURE_X"] = "on",
            ["GAME_API_KEY"] = "s3cr3t value",
        }));
        Assert.That(_warnings.Count, Is.EqualTo(2));
        string log = string.Join("\n", _warnings);
        Assert.That(log, Does.Contain("PORT").And.Contain("NEBULA_MESH_TOKEN"));
        Assert.That(log, Does.Not.Contain("hunter2").And.Not.Contain("s3cr3t"));
    }

    [Test]
    public void Resolve_without_a_manifest_env_or_file_is_empty_and_a_missing_file_warns()
    {
        Assert.That(WorkerEnvironment.Resolve(Env(new()), _warnings.Add), Is.Empty);
        Assert.That(_warnings, Is.Empty);
        Assert.That(WorkerEnvironment.Resolve(Env(new() { [NebulaEnv.EnvFileVariable] = Path.Combine(_dir, "missing.env") }), _warnings.Add), Is.Empty);
        Assert.That(_warnings.Single(), Does.Contain("does not exist"));
    }

    [Test]
    public void Process_host_puts_the_variables_in_the_worker_environment()
    {
        var vars = new Dictionary<string, string> { ["GAME_API_URL"] = "https://x", ["GAME_API_KEY"] = "k" };
        var psi = ProcessWorkerHost.StartInfo(Path.Combine(_dir, "Game.exe"), "-nebula-role worker", vars);
        Assert.That(psi.UseShellExecute, Is.False);
        Assert.That(psi.Environment["GAME_API_URL"], Is.EqualTo("https://x"));
        Assert.That(psi.Environment["GAME_API_KEY"], Is.EqualTo("k"));
        // Everything else is still inherited from the launcher.
        Assert.That(psi.Environment.ContainsKey("PATH") || psi.Environment.ContainsKey("Path"), Is.True);
    }

    [Test]
    public void Hetzner_boot_script_writes_a_root_only_env_file_the_worker_unit_reads()
    {
        var env = new Dictionary<string, string>
        {
            ["GAME_API_URL"] = "https://api.example.test/v1?a=1&b=2",
            ["TRICKY"] = "quote \" dollar $HOME backtick ` backslash \\",
            ["MULTI"] = "a\nb",
        };
        string script = HetznerWorkerHost.BuildCloudInit("http://10.0.0.2:7080/build", "-nebula-role worker -nebula-advertise $PRIVATE_IP", env, _warnings.Add);

        Assert.That(script, Does.Contain("(umask 077; cat > /etc/nebula/worker.env <<'NEBULA_WORKER_ENV'"));
        Assert.That(script, Does.Contain("GAME_API_URL=\"https://api.example.test/v1?a=1&b=2\""));
        Assert.That(script, Does.Contain("TRICKY=\"quote \\\" dollar \\$HOME backtick \\` backslash \\\\\""));
        Assert.That(script, Does.Contain("EnvironmentFile=-/etc/nebula/worker.env"));
        Assert.That(script, Does.Not.Contain("MULTI="));
        Assert.That(_warnings.Single(), Does.Contain("MULTI").And.Not.Contain("a\nb"));
        // The env file is written before the unit starts the worker.
        Assert.That(script.IndexOf("NEBULA_WORKER_ENV\n)", StringComparison.Ordinal), Is.LessThan(script.IndexOf("systemctl start nebula-worker", StringComparison.Ordinal)));
        Assert.That(script.IndexOf("NEBULA_WORKER_ENV\n)", StringComparison.Ordinal), Is.GreaterThan(0));

        // No variables: the file is still written (empty), so a stale one from an image never applies.
        string bare = HetznerWorkerHost.BuildCloudInit("http://10.0.0.2:7080/build", "-nebula-role worker");
        Assert.That(bare, Does.Contain("<<'NEBULA_WORKER_ENV'\nNEBULA_WORKER_ENV\n)"));
    }
}
