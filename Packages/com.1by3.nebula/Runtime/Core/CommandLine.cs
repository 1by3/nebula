using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// Tiny "-key value" / "-flag" parser over <see cref="Environment.GetCommandLineArgs"/>. A web build has no command
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
                var raw = Environment.GetCommandLineArgs();
                for (int i = 1; i < raw.Length; i++)
                {
                    if (!raw[i].StartsWith("-")) continue;
                    string key = raw[i].TrimStart('-');
                    string value = "";
                    if (i + 1 < raw.Length && !raw[i + 1].StartsWith("-"))
                    {
                        value = raw[i + 1];
                        i++;
                    }
                    _args[key] = value;
                }
                return _args;
#endif
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

        public static float GetFloat(string key, float fallback) => Args.TryGetValue(key, out var v) && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : fallback;

        public static bool GetBool(string key, bool fallback)
        {
            if (!Args.TryGetValue(key, out var v)) return fallback;
            if (v == "") return true;
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
