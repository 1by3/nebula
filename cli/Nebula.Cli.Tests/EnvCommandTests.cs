using System.Text.Json;
using System.Text.Json.Nodes;
using Nebula.Cli.Cloud;
using Nebula.Cli.Commands;
using Nebula.Cli.Core;
using NUnit.Framework;

namespace Nebula.Cli.Tests;

/// <summary>`nebula env` against the fake Cloud API and against deploy.env in nebula.json, plus the local-run pieces.</summary>
[TestFixture]
[NonParallelizable]
public class EnvCommandTests
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
        EnvCommand.StdinOverride = new StringReader("");
        _project = MakeProject("cloud");
        new CliConfig { Cloud = new CliConfig.CloudSettings { ApiUrl = _cloud.Url, AccessToken = "acc1", RefreshToken = "ref1", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) } }.Save();
    }

    [TearDown]
    public void TearDown()
    {
        _cloud.Dispose();
        EnvCommand.StdinOverride = null;
        Environment.SetEnvironmentVariable("NEBULA_CLI_HOME", null);
        Environment.SetEnvironmentVariable("NEBULA_CLOUD_API", null);
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_project, true); } catch { }
    }

    private static string MakeProject(string target)
    {
        string root = Path.Combine(Path.GetTempPath(), "nebula-cli-tests", "proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.1f1\n");
        string cloud = target == "cloud" ? ",\n  \"cloud\": { \"organization\": \"acme\", \"project\": \"my-game\", \"deployment\": \"production\" }" : "";
        File.WriteAllText(Path.Combine(root, "nebula.json"), "{\n  \"nebula\": \"0.1.0\",\n  \"executable\": \"Nebula\",\n  \"custom\": { \"keep\": true },\n" +
            "  \"deploy\": { \"target\": \"" + target + "\", \"workers\": 3 }" + cloud + "\n}\n");
        return root;
    }

    private Cli.Result Env(params string[] args) => Cli.Run(new[] { "env" }.Concat(args).Concat(new[] { "--project", _project }).ToArray());

    private JsonNode ProjectJson() => JsonNode.Parse(File.ReadAllText(Path.Combine(_project, "nebula.json")))!;

    private IEnumerable<FakeCloud.Request> EnvRequests() => _cloud.Requests.Where(r => r.Path.Contains("/env"));

    // --- cloud: set ------------------------------------------------------------------------------------------

    [Test]
    public void Set_puts_a_plain_variable_for_every_deployment_of_the_project()
    {
        var r = Env("set", "GAME_API_URL=https://api.example.test/v1?a=1");
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        var put = _cloud.Of("PUT", "/v1/projects/prj_1/env/GAME_API_URL").Single();
        Assert.That(put.Header("Authorization"), Is.EqualTo("Bearer acc1"));
        Assert.That(put.Json!["value"]!.ToString(), Is.EqualTo("https://api.example.test/v1?a=1"));
        Assert.That((bool)put.Json["secret"]!, Is.False);
        Assert.That(put.Json["deployments"]!.AsArray(), Is.Empty);
        Assert.That(r.Out, Does.Contain("GAME_API_URL set for all deployments of acme/my-game"));
    }

    [Test]
    public void Set_reads_a_secret_from_stdin_and_never_prints_it()
    {
        EnvCommand.StdinOverride = new StringReader("s3cr3t-value\n");
        var r = Env("set", "GAME_API_KEY", "--secret", "--deployment", "production,staging");
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        var put = _cloud.Of("PUT", "/v1/projects/prj_1/env/GAME_API_KEY").Single();
        Assert.That(put.Json!["value"]!.ToString(), Is.EqualTo("s3cr3t-value"), "one trailing line break is dropped");
        Assert.That((bool)put.Json["secret"]!, Is.True);
        Assert.That(put.Json["deployments"]!.AsArray().Select(d => d!.ToString()), Is.EqualTo(new[] { "production", "staging" }));
        Assert.That(r.All, Does.Not.Contain("s3cr3t"));
        Assert.That(r.All, Does.Not.Contain("shell history"));
        Assert.That(r.Out, Does.Contain("GAME_API_KEY set (secret) for production, staging"));
        Assert.That(r.Out, Does.Contain("project my-game has no deployment 'staging' yet"), "an unknown deployment is a warning, not an error");
    }

    [Test]
    public void Set_warns_when_a_secret_is_on_the_command_line()
    {
        var r = Env("set", "GAME_API_KEY=abc", "--secret");
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        Assert.That(r.Out, Does.Contain("shell history"));
        Assert.That(r.All, Does.Not.Contain("abc"));
    }

    [TestCase("PORT=8080", "PORT is reserved")]
    [TestCase("port=8080", "reserved")]
    [TestCase("NEBULA_MESH_TOKEN=x", "names starting with NEBULA_")]
    [TestCase("1BAD=x", "not a valid variable name")]
    [TestCase("WITH-DASH=x", "not a valid variable name")]
    public void Set_refuses_reserved_and_invalid_names_before_calling_the_api(string arg, string message)
    {
        var r = Env("set", arg);
        Assert.That(r.Code, Is.EqualTo(1), r.All);
        Assert.That(r.Err, Does.Contain(message));
        Assert.That(EnvRequests(), Is.Empty);
    }

    [Test]
    public void Set_refuses_a_value_over_32_KiB()
    {
        EnvCommand.StdinOverride = new StringReader(new string('x', 32 * 1024 + 1));
        var r = Env("set", "BIG");
        Assert.That(r.Code, Is.EqualTo(1), r.All);
        Assert.That(r.Err, Does.Contain("32 KiB"));
        Assert.That(EnvRequests(), Is.Empty);
    }

    [Test]
    public void The_api_error_code_alone_is_understood()
    {
        var api = CloudApi.Require(new Context());
        var e = Assert.Throws<CloudApiError>(() => api.SetEnvVar("prj_1", "NEBULA_X", "v", false, new List<string>()))!;
        Assert.That(e.Status, Is.EqualTo(400));
        Assert.That(e.Code, Is.EqualTo("reserved_key"));
        Assert.That(e.Message, Does.Contain("reserved key"));
    }

    // --- cloud: ls / rm ----------------------------------------------------------------------------------------

    [Test]
    public void Ls_shows_plain_values_and_scopes_and_masks_secrets()
    {
        _cloud.Env["prj_1/GAME_API_URL"] = ("https://api.example.test", false, new List<string>());
        _cloud.Env["prj_1/GAME_API_KEY"] = ("hidden-value", true, new List<string> { "production" });
        var r = Env("ls");
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        Assert.That(r.Out, Does.Contain("GAME_API_URL").And.Contain("https://api.example.test").And.Contain("all deployments"));
        Assert.That(r.Out, Does.Contain("GAME_API_KEY").And.Contain("(secret)").And.Contain("production"));
        Assert.That(r.Out, Does.Not.Contain("hidden-value"));

        var json = Env("ls", "--json", "--deployment", "production");
        Assert.That(json.Code, Is.EqualTo(0), json.All);
        Assert.That(_cloud.Requests.Last(r2 => r2.Path == "/v1/projects/prj_1/env").Param("deployment"), Is.EqualTo("production"));
        var vars = JsonNode.Parse(json.Out)!.AsArray();
        Assert.That(vars.Count, Is.EqualTo(2));
        Assert.That(vars.Single(v => v!["key"]!.ToString() == "GAME_API_KEY")!["value"], Is.Null);
    }

    [Test]
    public void Rm_removes_a_variable_everywhere_or_from_named_deployments()
    {
        _cloud.Env["prj_1/A"] = ("1", false, new List<string>());
        _cloud.Env["prj_1/B"] = ("2", false, new List<string> { "production", "staging" });
        Assert.That(Env("rm", "A").Code, Is.EqualTo(0));
        Assert.That(_cloud.Of("DELETE", "/v1/projects/prj_1/env/A").Single().Query, Is.Empty);
        var r = Env("rm", "B", "--deployment", "staging");
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        Assert.That(_cloud.Of("DELETE", "/v1/projects/prj_1/env/B").Single().Param("deployment"), Is.EqualTo("staging"));
        Assert.That(r.Out, Does.Contain("B removed from staging"));
        Assert.That(_cloud.Env.ContainsKey("prj_1/A"), Is.False);
        Assert.That(_cloud.Env["prj_1/B"].Deployments, Is.EqualTo(new[] { "production" }));

        var missing = Env("rm", "NOPE");
        Assert.That(missing.Code, Is.EqualTo(1));
        Assert.That(missing.Err, Does.Contain("NOPE is not set"));
    }

    [Test]
    public void A_project_without_a_cloud_section_is_refused_with_a_hint()
    {
        File.WriteAllText(Path.Combine(_project, "nebula.json"), "{ \"deploy\": { \"target\": \"cloud\" } }");
        var r = Env("ls");
        Assert.That(r.Code, Is.EqualTo(1));
        Assert.That(r.Err, Does.Contain("no Nebula Cloud project in nebula.json"));
    }

    // --- cloud: pull / push --------------------------------------------------------------------------------------

    [Test]
    public void Pull_writes_plain_values_as_dotenv_and_leaves_secrets_out()
    {
        _cloud.Env["prj_1/GAME_API_URL"] = ("https://api.example.test", false, new List<string>());
        _cloud.Env["prj_1/MOTD"] = ("Hello \"pilots\" # welcome\nline two", false, new List<string>());
        _cloud.Env["prj_1/GAME_API_KEY"] = ("hidden-value", true, new List<string>());
        var r = Env("pull", "--deployment", "production");
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        Assert.That(_cloud.Requests.Last(x => x.Path == "/v1/projects/prj_1/env").Param("deployment"), Is.EqualTo("production"));
        Assert.That(r.Out, Does.Not.Contain("hidden-value"));
        Assert.That(r.Out, Does.Contain("# GAME_API_KEY=  (secret)"));
        var parsed = DotEnv.Parse(r.Out);
        Assert.That(parsed.Errors, Is.Empty);
        Assert.That(parsed.ToDictionary(), Is.EquivalentTo(new Dictionary<string, string>
        {
            ["GAME_API_URL"] = "https://api.example.test",
            ["MOTD"] = "Hello \"pilots\" # welcome\nline two",
        }));

        string file = Path.Combine(_project, LocalEnv.FileName);
        var toFile = Env("pull", "--out", file);
        Assert.That(toFile.Code, Is.EqualTo(0), toFile.All);
        Assert.That(toFile.Out, Does.Contain("wrote 2 variable(s)").And.Contain("1 secret(s) left out"));
        Assert.That(DotEnv.Parse(File.ReadAllText(file)).ToDictionary()["GAME_API_URL"], Is.EqualTo("https://api.example.test"));
    }

    [Test]
    public void Push_uploads_a_dotenv_file_in_one_call_with_the_named_secrets()
    {
        string file = Path.Combine(_project, "prod.env");
        File.WriteAllText(file, "# production\nexport GAME_API_URL=https://api.example.test\nGAME_API_KEY='k3y'\nFEATURE_X=on # flag\n");
        var r = Env("push", file, "--deployment", "production", "--secret-keys", "GAME_API_KEY");
        Assert.That(r.Code, Is.EqualTo(0), r.All);
        var put = _cloud.Of("PUT", "/v1/projects/prj_1/env").Single();
        Assert.That(put.Json!["deployments"]!.AsArray().Select(d => d!.ToString()), Is.EqualTo(new[] { "production" }));
        var vars = put.Json["vars"]!.AsArray().ToDictionary(v => v!["key"]!.ToString(), v => (Value: v!["value"]!.ToString(), Secret: (bool)v["secret"]!));
        Assert.That(vars["GAME_API_URL"], Is.EqualTo(("https://api.example.test", false)));
        Assert.That(vars["GAME_API_KEY"], Is.EqualTo(("k3y", true)));
        Assert.That(vars["FEATURE_X"], Is.EqualTo(("on", false)));
        Assert.That(r.Out, Does.Contain("pushed 3 variable(s) (1 secret)"));
        Assert.That(r.All, Does.Not.Contain("k3y"));
    }

    [TestCase("GOOD=1\nPORT=80\n", "PORT is reserved")]
    [TestCase("GOOD=1\nnot a line\n", "line 2")]
    public void Push_uploads_nothing_when_the_file_has_a_problem(string text, string message)
    {
        string file = Path.Combine(_project, "bad.env");
        File.WriteAllText(file, text);
        var r = Env("push", file);
        Assert.That(r.Code, Is.EqualTo(1), r.All);
        Assert.That(r.Err, Does.Contain(message));
        Assert.That(EnvRequests(), Is.Empty);
    }

    [Test]
    public void Push_refuses_secret_keys_that_are_not_in_the_file()
    {
        string file = Path.Combine(_project, "x.env");
        File.WriteAllText(file, "A=1\n");
        var r = Env("push", file, "--secret-keys", "A,B");
        Assert.That(r.Code, Is.EqualTo(1));
        Assert.That(r.Err, Does.Contain("B, which is not in"));
        Assert.That(EnvRequests(), Is.Empty);
    }

    // --- self-hosted: deploy.env in nebula.json ---------------------------------------------------------------------

    [Test]
    public void Self_hosted_variables_live_in_deploy_env_and_never_touch_the_cloud()
    {
        _project = MakeProject("hetzner");
        Assert.That(Env("set", "GAME_API_URL=https://self.example.test").Code, Is.EqualTo(0));
        Assert.That(Env("set", "FEATURE_X=on").Code, Is.EqualTo(0));
        var json = ProjectJson();
        Assert.That(json["deploy"]!["env"]!["GAME_API_URL"]!.ToString(), Is.EqualTo("https://self.example.test"));
        Assert.That(json["custom"]!["keep"]!.GetValue<bool>(), Is.True, "unknown keys survive the save");

        var ls = Env("ls");
        Assert.That(ls.Out, Does.Contain("FEATURE_X").And.Contain("on").And.Contain("every worker"));

        var pull = Env("pull");
        Assert.That(DotEnv.Parse(pull.Out).ToDictionary().Keys, Is.EquivalentTo(new[] { "FEATURE_X", "GAME_API_URL" }));

        string file = Path.Combine(_project, "more.env");
        File.WriteAllText(file, "EXTRA=1\n");
        Assert.That(Env("push", file).Code, Is.EqualTo(0));
        Assert.That(ProjectJson()["deploy"]!["env"]!["EXTRA"]!.ToString(), Is.EqualTo("1"));

        Assert.That(Env("rm", "FEATURE_X").Code, Is.EqualTo(0));
        Assert.That(ProjectJson()["deploy"]!["env"]!["FEATURE_X"], Is.Null);
        Assert.That(_cloud.Requests, Is.Empty);
    }

    [TestCase("set", "KEY=1", "--secret")]
    [TestCase("set", "KEY=1", "--deployment", "staging")]
    public void Self_hosted_refuses_secrets_and_deployments(params string[] args)
    {
        _project = MakeProject("hetzner");
        var r = Env(args);
        Assert.That(r.Code, Is.EqualTo(1), r.All);
        Assert.That(ProjectJson()["deploy"]!["env"], Is.Null);
    }

    [Test]
    public void The_manifest_carries_deploy_env_as_its_Env_map()
    {
        string manifest = "{\"Version\":1,\"Config\":{\"GatewayPort\":7000},\"Containers\":[],\"Env\":{\"OLD\":\"x\"}}";
        var env = new SortedDictionary<string, string> { ["GAME_API_URL"] = "https://x", ["MOTD"] = "a \"b\"" };
        var node = JsonNode.Parse(HetznerMesh.WithEnv(manifest, env))!;
        Assert.That(node["Config"]!["GatewayPort"]!.GetValue<int>(), Is.EqualTo(7000));
        Assert.That(node["Env"]!.AsObject().Select(kv => kv.Key), Is.EquivalentTo(new[] { "GAME_API_URL", "MOTD" }));
        Assert.That(node["Env"]!["MOTD"]!.ToString(), Is.EqualTo("a \"b\""));
        Assert.That(JsonNode.Parse(HetznerMesh.WithEnv(manifest, null))!["Env"], Is.Null, "an empty deploy.env clears what an earlier deploy wrote");
        Assert.Throws<CliError>(() => HetznerMesh.WithEnv(manifest, new Dictionary<string, string> { ["PORT"] = "1" }));
    }

    // --- local runs --------------------------------------------------------------------------------------------------

    [Test]
    public void Init_adds_env_nebula_to_an_existing_gitignore_once()
    {
        string gitignore = Path.Combine(_project, ".gitignore");
        File.WriteAllText(gitignore, "/Library/\n/Temp/");
        InitCommand.IgnoreLocalEnv(_project);
        InitCommand.IgnoreLocalEnv(_project);
        string text = File.ReadAllText(gitignore);
        Assert.That(text, Does.StartWith("/Library/\n/Temp/\n"));
        Assert.That(text.Split('\n').Count(l => l == "/.env.nebula"), Is.EqualTo(1));
    }

    [Test]
    public void Local_env_check_skips_reserved_names_and_prints_no_values()
    {
        File.WriteAllText(Path.Combine(_project, LocalEnv.FileName), "GAME_API_URL=http://localhost:5000\nPORT=1234\nNEBULA_TOKEN=topsecret\n");
        var stdout = new StringWriter();
        var old = Console.Out;
        Console.SetOut(stdout);
        Dictionary<string, string>? vars;
        try { vars = LocalEnv.Check(NebulaProject.Load(_project)); }
        finally { Console.SetOut(old); }
        Assert.That(vars!.Keys, Is.EquivalentTo(new[] { "GAME_API_URL" }));
        Assert.That(stdout.ToString(), Does.Contain("PORT").And.Contain("NEBULA_TOKEN"));
        Assert.That(stdout.ToString(), Does.Not.Contain("topsecret").And.Not.Contain("1234"));
    }
}
