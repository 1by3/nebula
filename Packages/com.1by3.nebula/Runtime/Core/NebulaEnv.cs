#nullable disable
using System;
using System.Collections.Generic;
using System.IO;

namespace Nebula
{
    /// <summary>
    /// A game's own settings and secrets on a worker (service URLs, API keys, feature flags): the environment
    /// variables set with <c>nebula env</c>, in the Nebula Cloud dashboard, or in <c>.env.nebula</c> for local runs.
    /// </summary>
    /// <remarks>
    /// <see cref="Get"/> looks a variable up in this order and returns the first value it finds:
    /// <list type="number">
    /// <item>The command line: <c>-holoverse-economy-uri &lt;value&gt;</c> (or <c>=value</c>) for <c>HOLOVERSE_ECONOMY_URI</c>.
    /// The switch is the name in lower case with <c>_</c> turned into <c>-</c> (<see cref="ArgName"/>). A web build
    /// reads it from the page's query string instead.</item>
    /// <item>The process environment: <c>HOLOVERSE_ECONOMY_URI</c> (<see cref="EnvName"/>). Deployed workers get their
    /// variables here: Nebula Cloud and <c>nebula deploy</c> set them before the worker starts.</item>
    /// <item>The files Nebula loaded for this process: the file <c>NEBULA_ENV_FILE</c> names, when the process that
    /// started the worker did not apply it, and in the Editor the project's <c>.env.nebula</c>.</item>
    /// <item><c>fallback</c>.</item>
    /// </list>
    /// Either spelling of a name works: <c>Get("HOLOVERSE_ECONOMY_URI")</c> and <c>Get("holoverse-economy-uri")</c>
    /// look up the same variable. Nebula never logs a variable's value.
    /// </remarks>
    public static class NebulaEnv
    {
        /// <summary>The file a project keeps its local variables in, at the project root. Keep it out of version control.</summary>
        public const string LocalFileName = ".env.nebula";

        /// <summary>Names a dotenv file whose variables are applied to a worker on top of the deployment's own (Nebula Cloud writes its secrets there).</summary>
        public const string EnvFileVariable = "NEBULA_ENV_FILE";

        /// <summary>The keys this process copied into its own environment from <see cref="LocalFileName"/>, comma-separated, so the next load replaces them instead of treating them as set by the user.</summary>
        internal const string LoadedKeysVariable = "NEBULA_DOTENV_KEYS";

        private static readonly object Gate = new object();
        private static Dictionary<string, string> _loaded = new Dictionary<string, string>(StringComparer.Ordinal);
        private static Dictionary<string, string> _envFile;
        private static string _envFilePath;

        /// <summary>Where warnings go (names and line numbers only, never values). Set by the runtime to its log.</summary>
        public static Action<string> Warn = message => { };

        /// <summary>
        /// The value of <paramref name="key"/> from the command line, the process environment or a loaded file, in
        /// that order, or <paramref name="fallback"/>.
        /// </summary>
        public static string Get(string key, string fallback = null)
        {
            if (string.IsNullOrWhiteSpace(key)) return fallback;
            string arg = CommandLine.Get(ArgName(key));
            if (arg != null) return arg;
            string env = EnvName(key);
            string fromProcess = Environment.GetEnvironmentVariable(env);
            if (fromProcess != null) return fromProcess;
            lock (Gate)
            {
                var file = EnvFileVars();
                if (file != null && file.TryGetValue(env, out var v)) return v;
                if (_loaded.TryGetValue(env, out v)) return v;
            }
            return fallback;
        }

        /// <summary>Whether <see cref="Get"/> would find <paramref name="key"/> anywhere.</summary>
        public static bool Has(string key) => Get(key) != null;

        /// <summary>The environment variable name for a key: upper case, <c>-</c> turned into <c>_</c>. <c>holoverse-economy-uri</c> is <c>HOLOVERSE_ECONOMY_URI</c>.</summary>
        public static string EnvName(string key) => (key ?? "").Trim().TrimStart('-').ToUpperInvariant().Replace('-', '_');

        /// <summary>The command-line switch for a key, without its dash: lower case, <c>_</c> turned into <c>-</c>. <c>HOLOVERSE_ECONOMY_URI</c> is <c>holoverse-economy-uri</c>.</summary>
        public static string ArgName(string key) => (key ?? "").Trim().TrimStart('-').ToLowerInvariant().Replace('_', '-');

        /// <summary>
        /// Read a dotenv file into the file layer of <see cref="Get"/> and copy its variables into this process's
        /// environment, where the environment does not already have them, so code that reads
        /// <see cref="Environment.GetEnvironmentVariable(string)"/> sees them too. Variables a previous call copied
        /// are replaced or removed. Reserved names (<c>PORT</c>, <c>NEBULA_*</c>) are skipped with a warning.
        /// </summary>
        /// <returns>How many variables were loaded; 0 when the file does not exist.</returns>
        public static int LoadFile(string path)
        {
            Dictionary<string, string> vars = null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { vars = ReadFile(path, Warn); }
                catch (Exception e) { Warn($"could not read {Path.GetFileName(path)}: {e.GetType().Name}"); }
            }
            vars = vars ?? new Dictionary<string, string>(StringComparer.Ordinal);
            lock (Gate)
            {
                // What the last load copied into the environment is ours to replace; anything else was set by the user and wins.
                var previous = new HashSet<string>(StringComparer.Ordinal);
                string listed = Environment.GetEnvironmentVariable(LoadedKeysVariable);
                if (!string.IsNullOrEmpty(listed)) foreach (var k in listed.Split(',')) if (k.Length > 0) previous.Add(k);
                foreach (var k in previous)
                    if (!vars.ContainsKey(k)) TrySetEnvironment(k, null);
                var copied = new List<string>();
                foreach (var kv in vars)
                {
                    if (!previous.Contains(kv.Key) && Environment.GetEnvironmentVariable(kv.Key) != null) continue;
                    if (TrySetEnvironment(kv.Key, kv.Value)) copied.Add(kv.Key);
                }
                TrySetEnvironment(LoadedKeysVariable, copied.Count > 0 ? string.Join(",", copied) : null);
                _loaded = vars;
            }
            return vars.Count;
        }

        /// <summary>Forget everything loaded (tests).</summary>
        internal static void Reset()
        {
            lock (Gate)
            {
                _loaded = new Dictionary<string, string>(StringComparer.Ordinal);
                _envFile = null;
                _envFilePath = null;
            }
        }

        /// <summary>
        /// A dotenv file's game variables. Unreadable lines, reserved names and values that are too long are left out,
        /// each with a warning that names the line or the key.
        /// </summary>
        public static Dictionary<string, string> ReadFile(string path, Action<string> warn)
        {
            string name = Path.GetFileName(path);
            var parsed = DotEnv.Parse(File.ReadAllText(path));
            foreach (var error in parsed.Errors) warn?.Invoke($"{name}: {error}");
            return Filter(parsed.ToDictionary(), name, warn);
        }

        /// <summary><paramref name="vars"/> without the names a game may not set and the values that are too long, with a warning for each (named by <paramref name="source"/>).</summary>
        public static Dictionary<string, string> Filter(IDictionary<string, string> vars, string source, Action<string> warn)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (vars == null) return result;
            foreach (var kv in vars)
            {
                string problem = DotEnv.KeyProblem(kv.Key) ?? DotEnv.ValueProblem(kv.Key, kv.Value);
                if (problem != null)
                {
                    warn?.Invoke($"{source}: ignoring {problem}");
                    continue;
                }
                result[kv.Key] = kv.Value ?? "";
            }
            return result;
        }

        /// <summary>The file NEBULA_ENV_FILE names, read once per path; null when it is unset or unreadable.</summary>
        private static Dictionary<string, string> EnvFileVars()
        {
            string path = Environment.GetEnvironmentVariable(EnvFileVariable);
            if (string.IsNullOrEmpty(path)) return null;
            if (path == _envFilePath) return _envFile;
            _envFilePath = path;
            _envFile = null;
            try { if (File.Exists(path)) _envFile = ReadFile(path, Warn); }
            // A worker that runs as another user than the one that owns the file cannot read it: the launcher applied it, or nothing did.
            catch (Exception) { }
            return _envFile;
        }

        private static bool TrySetEnvironment(string key, string value)
        {
            try
            {
                Environment.SetEnvironmentVariable(key, value);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
