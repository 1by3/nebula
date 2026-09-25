#nullable disable
using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// Tiny "-key value" / "-key=value" / "-flag" parser over <see cref="Environment.GetCommandLineArgs"/>. A web build has no command
    /// line and reads the same switches from the page's query string instead: <c>?nebula-name=Jesse&amp;nebula-connect</c>.
    /// </summary>
    public static class CommandLine
    {
        private static Dictionary<string, string> _args;

        private static Dictionary<string, string> Args
        {
            get
            {
                if (_args != null) return _args;
                _args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
#if UNITY_WEBGL && !UNITY_EDITOR
                ParseQuery(UnityEngine.Application.absoluteURL, _args);
                return _args;
#else
                ParseArgs(Environment.GetCommandLineArgs(), 1, _args);
                return _args;
#endif
            }
        }

        /// <summary>
        /// Adds switches from an argument vector to <paramref name="into"/>, starting at <paramref name="start"/>
        /// (1 skips the executable). Both spellings work: <c>-key value</c> and <c>-key=value</c>; the second is the
        /// only way to pass a value that itself starts with a dash (<c>-heading=-30</c>). A switch with no value
        /// maps to the empty string.
        /// </summary>
        public static void ParseArgs(IReadOnlyList<string> raw, int start, IDictionary<string, string> into)
        {
            for (int i = start; i < raw.Count; i++)
            {
                string arg = raw[i];
                if (arg == null || !arg.StartsWith("-")) continue;
                string key = arg.TrimStart('-');
                string value = "";
                int eq = key.IndexOf('=');
                if (eq >= 0)
                {
                    value = key.Substring(eq + 1);
                    key = key.Substring(0, eq);
                }
                else if (i + 1 < raw.Count && raw[i + 1] != null && !raw[i + 1].StartsWith("-"))
                {
                    value = raw[i + 1];
                    i++;
                }
                if (key.Length > 0) into[key] = value;
            }
        }

        /// <summary>Adds the parameters of a URL's query string ("a=1&amp;b") to <paramref name="into"/>, URL-decoded, with any leading dashes removed from the keys.</summary>
        public static void ParseQuery(string url, IDictionary<string, string> into)
        {
            if (string.IsNullOrEmpty(url)) return;
            int start = url.IndexOf('?');
            if (start < 0) return;
            int end = url.IndexOf('#', start);
            string query = end < 0 ? url.Substring(start + 1) : url.Substring(start + 1, end - start - 1);
            foreach (string pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key = Uri.UnescapeDataString((eq < 0 ? pair : pair.Substring(0, eq)).Replace('+', ' ')).TrimStart('-');
                if (key.Length == 0) continue;
                into[key] = eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
            }
        }

        /// <summary>Switches whose value is a credential: <see cref="Redact"/> hides them.</summary>
        public static readonly string[] SecretSwitches =
        {
            "nebula-token", "nebula-auth-key", "nebula-cloud-token", "nebula-cloud-deployment-token", "nebula-database",
        };

        /// <summary>
        /// A command line as text, for a log line, with the values of <see cref="SecretSwitches"/> replaced by
        /// <c>***</c>. Handles <c>-key value</c> and <c>-key=value</c>; a value in quotes is hidden whole.
        /// </summary>
        public static string Redact(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return commandLine;
            var sb = new System.Text.StringBuilder(commandLine.Length);
            bool hideNext = false;
            int i = 0;
            while (i < commandLine.Length)
            {
                if (char.IsWhiteSpace(commandLine[i])) { sb.Append(commandLine[i++]); continue; }
                int start = i;
                bool quoted = false;
                while (i < commandLine.Length && (quoted || !char.IsWhiteSpace(commandLine[i])))
                {
                    if (commandLine[i] == '"') quoted = !quoted;
                    i++;
                }
                string token = commandLine.Substring(start, i - start);
                if (hideNext && !token.StartsWith("-")) { sb.Append("***"); hideNext = false; continue; }
                hideNext = false;
                if (token.StartsWith("-"))
                {
                    string key = token.TrimStart('-');
                    int eq = key.IndexOf('=');
                    string name = eq >= 0 ? key.Substring(0, eq) : key;
                    if (Array.Exists(SecretSwitches, s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (eq >= 0) { sb.Append(token, 0, token.IndexOf('=') + 1).Append("***"); continue; }
                        hideNext = true;
                    }
                }
                sb.Append(token);
            }
            return sb.ToString();
        }

        /// <summary>For tests/editor: pretend these arguments were passed.</summary>
        public static void Override(IDictionary<string, string> values)
        {
            _args = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }

        public static bool Has(string key) => Args.ContainsKey(key);

        /// <summary>Every switch that was parsed, for a service that forwards a whole family of them (<c>-nebula-ext-*</c>) to a process it launches.</summary>
        public static IReadOnlyCollection<string> Keys => Args.Keys;

        public static string Get(string key, string fallback = null) => Args.TryGetValue(key, out var v) ? v : fallback;

        public static int GetInt(string key, int fallback) => Args.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;
        public static uint GetUInt(string key, uint fallback) => Args.TryGetValue(key, out var v) && uint.TryParse(v, out var i) ? i : fallback;

        public static float GetFloat(string key, float fallback) => Args.TryGetValue(key, out var v) && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : fallback;

        public static bool GetBool(string key, bool fallback)
        {
            if (!Args.TryGetValue(key, out var v)) return fallback;
            if (v == "") return true;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
