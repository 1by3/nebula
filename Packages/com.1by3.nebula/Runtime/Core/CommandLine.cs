using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>Tiny "-key value" / "-flag" parser over <see cref="Environment.GetCommandLineArgs"/>.</summary>
    public static class CommandLine
    {
        private static Dictionary<string, string> _args;

        private static Dictionary<string, string> Args
        {
            get
            {
                if (_args != null) return _args;
                _args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
            }
        }

        /// <summary>For tests/editor: pretend these arguments were passed.</summary>
        public static void Override(IDictionary<string, string> values)
        {
            _args = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }

        public static bool Has(string key) => Args.ContainsKey(key);

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
