using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The dotenv format, the variable-name rules, and <see cref="NebulaEnv.Get"/>'s lookup order. Pure C#: the
    /// standalone services run these too.
    /// </summary>
    public sealed class NebulaEnvTests
    {
        private readonly List<string> _vars = new List<string>();
        private readonly List<string> _files = new List<string>();
        private readonly List<string> _warnings = new List<string>();

        [SetUp]
        public void SetUp()
        {
            NebulaEnv.Reset();
            NebulaEnv.Warn = _warnings.Add;
            CommandLine.Override(new Dictionary<string, string>());
            Environment.SetEnvironmentVariable(NebulaEnv.EnvFileVariable, null);
            Environment.SetEnvironmentVariable(NebulaEnv.LoadedKeysVariable, null);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var v in _vars) Environment.SetEnvironmentVariable(v, null);
            foreach (var f in _files) try { File.Delete(f); } catch { }
            Environment.SetEnvironmentVariable(NebulaEnv.EnvFileVariable, null);
            Environment.SetEnvironmentVariable(NebulaEnv.LoadedKeysVariable, null);
            NebulaEnv.Reset();
            NebulaEnv.Warn = m => { };
            CommandLine.Override(new Dictionary<string, string>());
        }

        private string Var(string suffix)
        {
            string name = "NEBTEST_" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant() + "_" + suffix;
            _vars.Add(name);
            return name;
        }

        private string TempFile(string text)
        {
            string path = Path.Combine(Path.GetTempPath(), "nebula-env-" + Guid.NewGuid().ToString("N") + ".env");
            File.WriteAllText(path, text);
            _files.Add(path);
            return path;
        }

        // ------------------------------------------------------------------------------------------ parsing

        [Test]
        public void Parse_reads_bare_quoted_exported_and_commented_lines()
        {
            var r = DotEnv.Parse(
                "# a comment\n" +
                "\n" +
                "PLAIN=hello\n" +
                "SPACED = padded value   \n" +
                "export EXPORTED=yes\n" +
                "TRAILING=value # a comment\n" +
                "HASH=a#b\n" +
                "SINGLE='literal \\n $HOME # not a comment'\n" +
                "DOUBLE=\"line1\\nline2 \\\"q\\\" \\\\ \\t\"\n" +
                "EMPTY=\n" +
                "EMPTYQ=\"\"\n" +
                "URL=postgres://u:p@host:5432/db?sslmode=require\n");
            Assert.That(r.Errors, Is.Empty);
            var d = r.ToDictionary();
            Assert.That(d["PLAIN"], Is.EqualTo("hello"));
            Assert.That(d["SPACED"], Is.EqualTo("padded value"));
            Assert.That(d["EXPORTED"], Is.EqualTo("yes"));
            Assert.That(d["TRAILING"], Is.EqualTo("value"));
            Assert.That(d["HASH"], Is.EqualTo("a#b"));
            Assert.That(d["SINGLE"], Is.EqualTo("literal \\n $HOME # not a comment"));
            Assert.That(d["DOUBLE"], Is.EqualTo("line1\nline2 \"q\" \\ \t"));
            Assert.That(d["EMPTY"], Is.EqualTo(""));
            Assert.That(d["EMPTYQ"], Is.EqualTo(""));
            Assert.That(d["URL"], Is.EqualTo("postgres://u:p@host:5432/db?sslmode=require"));
        }

        [Test]
        public void Parse_reads_multiline_quoted_values_and_crlf()
        {
            var r = DotEnv.Parse("KEY=\"-----BEGIN KEY-----\r\nabc\r\n-----END KEY-----\"\r\nNEXT='a\r\nb' # done\r\nLAST=1\r\n");
            Assert.That(r.Errors, Is.Empty);
            var d = r.ToDictionary();
            Assert.That(d["KEY"], Is.EqualTo("-----BEGIN KEY-----\nabc\n-----END KEY-----"));
            Assert.That(d["NEXT"], Is.EqualTo("a\nb"));
            Assert.That(d["LAST"], Is.EqualTo("1"));
        }

        [Test]
        public void Parse_reports_bad_lines_by_number_without_values_and_keeps_going()
        {
            var r = DotEnv.Parse("GOOD=1\nnot a variable\n1BAD=secretvalue\nALSO GOOD=2\nOK=3\nOPEN=\"never closed secretvalue\nMORE=4\n");
            Assert.That(r.ToDictionary().Keys, Is.EquivalentTo(new[] { "GOOD", "OK" }));
            Assert.That(r.Errors.Count, Is.EqualTo(4));
            Assert.That(r.Errors[0], Does.StartWith("line 2:"));
            Assert.That(r.Errors[1], Does.StartWith("line 3:"));
            Assert.That(string.Join("\n", r.Errors), Does.Not.Contain("secretvalue"));
        }

        [Test]
        public void Parse_last_duplicate_wins_and_a_bom_is_ignored()
        {
            var d = DotEnv.Parse("\uFEFFKEY=1\nKEY=2\n").ToDictionary();
            Assert.That(d["KEY"], Is.EqualTo("2"));
        }

        [TestCase("simple")]
        [TestCase("")]
        [TestCase("with spaces and # hash")]
        [TestCase("quotes \" and ' and \\ backslash")]
        [TestCase("multi\nline\r\nvalue\twith tab")]
        [TestCase("  leading and trailing  ")]
        [TestCase("postgres://user:p%40ss@host:5432/db")]
        public void Format_round_trips_through_parse(string value)
        {
            string text = DotEnv.Write(new[] { new KeyValuePair<string, string>("KEY", value) }, new[] { "header" });
            var r = DotEnv.Parse(text);
            Assert.That(r.Errors, Is.Empty, text);
            Assert.That(r.ToDictionary()["KEY"], Is.EqualTo(value), text);
        }

        // ------------------------------------------------------------------------------------------ names

        [TestCase("GAME_URL", true)]
        [TestCase("_private", true)]
        [TestCase("a1", true)]
        [TestCase("1A", false)]
        [TestCase("WITH-DASH", false)]
        [TestCase("WITH SPACE", false)]
        [TestCase("", false)]
        public void Valid_keys_are_letters_digits_and_underscores(string key, bool valid)
        {
            Assert.That(DotEnv.IsValidKey(key), Is.EqualTo(valid));
        }

        [TestCase("PORT")]
        [TestCase("port")]
        [TestCase("NEBULA_ENV_FILE")]
        [TestCase("nebula_anything")]
        public void Port_and_nebula_names_are_reserved(string key)
        {
            Assert.That(DotEnv.IsReserved(key), Is.True);
            Assert.That(DotEnv.KeyProblem(key), Does.Contain("reserved"));
        }

        [Test]
        public void Values_over_32_KiB_are_refused()
        {
            Assert.That(DotEnv.ValueProblem("K", new string('x', DotEnv.MaxValueBytes)), Is.Null);
            Assert.That(DotEnv.ValueProblem("K", new string('x', DotEnv.MaxValueBytes + 1)), Does.Contain("32 KiB"));
        }

        [Test]
        public void Arg_and_env_names_map_both_ways()
        {
            Assert.That(NebulaEnv.ArgName("GAME_API_URL"), Is.EqualTo("game-api-url"));
            Assert.That(NebulaEnv.EnvName("game-api-url"), Is.EqualTo("GAME_API_URL"));
            Assert.That(NebulaEnv.EnvName("-game-api-url"), Is.EqualTo("GAME_API_URL"));
            Assert.That(NebulaEnv.ArgName(NebulaEnv.EnvName("feature-x")), Is.EqualTo("feature-x"));
        }

        // ------------------------------------------------------------------------------------------ lookup order

        [Test]
        public void Get_prefers_the_command_line_then_the_environment_then_files_then_the_fallback()
        {
            string name = Var("URI");
            string arg = NebulaEnv.ArgName(name);

            Assert.That(NebulaEnv.Get(name, "fallback"), Is.EqualTo("fallback"));
            Assert.That(NebulaEnv.Get(name), Is.Null);

            string envFile = TempFile($"{name}=from-env-file\n");
            Environment.SetEnvironmentVariable(NebulaEnv.EnvFileVariable, envFile);
            Assert.That(NebulaEnv.Get(name, "fallback"), Is.EqualTo("from-env-file"));

            Environment.SetEnvironmentVariable(name, "from-process");
            Assert.That(NebulaEnv.Get(name, "fallback"), Is.EqualTo("from-process"));

            CommandLine.Override(new Dictionary<string, string> { [arg] = "from-args" });
            Assert.That(NebulaEnv.Get(name, "fallback"), Is.EqualTo("from-args"));
            // Either spelling of the name finds it.
            Assert.That(NebulaEnv.Get(arg), Is.EqualTo("from-args"));
        }

        [Test]
        public void Get_reads_a_loaded_local_file_below_the_process_environment()
        {
            string fromFile = Var("FILE");
            string both = Var("BOTH");
            Environment.SetEnvironmentVariable(both, "real");
            string path = TempFile($"{fromFile}=local\n{both}=from-file\n");
            Assert.That(NebulaEnv.LoadFile(path), Is.EqualTo(2));
            Assert.That(NebulaEnv.Get(fromFile), Is.EqualTo("local"));
            Assert.That(NebulaEnv.Get(both), Is.EqualTo("real"), "a variable the user set in the environment wins over the file");
            // Copied into the environment so plain Environment.GetEnvironmentVariable sees it too; the real one is untouched.
            Assert.That(Environment.GetEnvironmentVariable(fromFile), Is.EqualTo("local"));
            Assert.That(Environment.GetEnvironmentVariable(both), Is.EqualTo("real"));
        }

        [Test]
        public void Reloading_the_local_file_replaces_what_the_last_load_copied()
        {
            string a = Var("A");
            string b = Var("B");
            string path = TempFile($"{a}=1\n{b}=1\n");
            NebulaEnv.LoadFile(path);
            File.WriteAllText(path, $"{a}=2\n");
            NebulaEnv.LoadFile(path);
            Assert.That(NebulaEnv.Get(a), Is.EqualTo("2"), "an edited value applies on the next load");
            Assert.That(Environment.GetEnvironmentVariable(b), Is.Null, "a removed key leaves the environment");
            Assert.That(NebulaEnv.Get(b), Is.Null);
            File.Delete(path);
            NebulaEnv.LoadFile(path);
            Assert.That(Environment.GetEnvironmentVariable(a), Is.Null, "deleting the file clears what it set");
        }

        [Test]
        public void Files_skip_reserved_names_and_warn_without_values()
        {
            string ok = Var("OK");
            string path = TempFile($"{ok}=fine\nPORT=9999\nNEBULA_MESH_TOKEN=supersecret\n");
            Assert.That(NebulaEnv.LoadFile(path), Is.EqualTo(1));
            Assert.That(NebulaEnv.Get(ok), Is.EqualTo("fine"));
            Assert.That(_warnings.Count, Is.EqualTo(2));
            Assert.That(_warnings.Any(w => w.Contains("PORT")), Is.True);
            Assert.That(_warnings.Any(w => w.Contains("NEBULA_MESH_TOKEN")), Is.True);
            Assert.That(string.Join("\n", _warnings), Does.Not.Contain("supersecret").And.Not.Contain("9999"));
        }

        // ------------------------------------------------------------------------------------------ redaction

        [Test]
        public void Redact_hides_credential_switch_values_in_both_spellings()
        {
            string line = "-nebula-role worker -nebula-token abc123 -nebula-auth-key=\"k e y\" -nebula-database postgres://u:pw@h/db -nebula-port 7100";
            string redacted = CommandLine.Redact(line);
            Assert.That(redacted, Does.Not.Contain("abc123").And.Not.Contain("k e y").And.Not.Contain("pw@h"));
            Assert.That(redacted, Does.Contain("-nebula-role worker").And.Contain("-nebula-port 7100").And.Contain("-nebula-token ***").And.Contain("-nebula-auth-key=***"));
        }
    }
}
