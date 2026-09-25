#nullable disable
using System;
using System.Collections.Generic;
using System.Text;

namespace Nebula
{
    /// <summary>
    /// Reads and writes dotenv files (<c>.env.nebula</c>, the file <c>NEBULA_ENV_FILE</c> names, <c>nebula env pull</c>
    /// output), and holds the rules for the names of the environment variables a game gives its workers. The same
    /// code runs in the Unity runtime, the standalone services and the CLI.
    /// </summary>
    /// <remarks>
    /// The format, one variable per line:
    /// <list type="bullet">
    /// <item><c>KEY=value</c>. Spaces around the <c>=</c> and around an unquoted value are dropped, and so is an
    /// unquoted value's trailing comment (<c> # ...</c>, a <c>#</c> after whitespace).</item>
    /// <item><c>export KEY=value</c>: the <c>export</c> is ignored, so a file can be sourced by a shell too.</item>
    /// <item><c>KEY='value'</c>: taken literally. It may span several lines.</item>
    /// <item><c>KEY="value"</c>: <c>\n</c>, <c>\r</c>, <c>\t</c>, <c>\"</c> and <c>\\</c> are escapes. It may span several lines.</item>
    /// <item>Blank lines and lines starting with <c>#</c> are skipped. Nothing is expanded: <c>$OTHER</c> stays as written.</item>
    /// <item>When a key appears twice, the last one wins.</item>
    /// </list>
    /// </remarks>
    public static class DotEnv
    {
        /// <summary>The longest value a variable may have: 32 KiB of UTF-8.</summary>
        public const int MaxValueBytes = 32 * 1024;

        /// <summary>One variable read from a file, with the line it starts on.</summary>
        public sealed class Entry
        {
            public string Key;
            public string Value;
            public int Line;
        }

        /// <summary>What <see cref="Parse"/> read: the variables in file order (duplicates included), and one message per line it could not read.</summary>
        public sealed class Result
        {
            public readonly List<Entry> Entries = new List<Entry>();
            /// <summary>"line 3: ..." messages. They name lines and keys, never values.</summary>
            public readonly List<string> Errors = new List<string>();

            /// <summary>The variables as a map; a key that appears twice keeps its last value.</summary>
            public Dictionary<string, string> ToDictionary()
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var e in Entries) map[e.Key] = e.Value;
                return map;
            }
        }

        /// <summary>Parse dotenv text. Never throws: lines it cannot read are reported in <see cref="Result.Errors"/> and skipped.</summary>
        public static Result Parse(string text)
        {
            var result = new Result();
            if (string.IsNullOrEmpty(text)) return result;
            if (text[0] == '﻿') text = text.Substring(1);
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int lineNumber = i + 1;
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("export ", StringComparison.Ordinal) || line.StartsWith("export\t", StringComparison.Ordinal)) line = line.Substring(7).TrimStart();
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    result.Errors.Add($"line {lineNumber}: expected KEY=value");
                    continue;
                }
                string key = line.Substring(0, eq).Trim();
                if (!IsValidKey(key))
                {
                    result.Errors.Add($"line {lineNumber}: '{Printable(key)}' is not a valid variable name (letters, digits and _, not starting with a digit)");
                    continue;
                }
                string rest = line.Substring(eq + 1).TrimStart();
                string value;
                if (rest.Length > 0 && (rest[0] == '"' || rest[0] == '\''))
                {
                    char quote = rest[0];
                    // The value runs to the matching quote, which may be on a later line. The lines are rejoined with
                    // "\n" and the untrimmed text of the continuation lines is kept.
                    var raw = new StringBuilder(lines[i].Substring(lines[i].IndexOf(quote, lines[i].IndexOf('=')) + 1));
                    int close = FindClose(raw.ToString(), quote);
                    int j = i;
                    while (close < 0 && j + 1 < lines.Length)
                    {
                        j++;
                        raw.Append('\n').Append(lines[j]);
                        close = FindClose(raw.ToString(), quote);
                    }
                    if (close < 0)
                    {
                        result.Errors.Add($"line {lineNumber}: {key} has no closing {quote}");
                        break;
                    }
                    string body = raw.ToString(0, close);
                    string after = raw.ToString(close + 1, raw.Length - close - 1).Trim();
                    if (after.Length > 0 && after[0] != '#')
                    {
                        result.Errors.Add($"line {j + 1}: unexpected text after the closing quote of {key}");
                        i = j;
                        continue;
                    }
                    value = quote == '"' ? Unescape(body) : body;
                    i = j;
                }
                else
                {
                    int hash = IndexOfComment(rest);
                    value = (hash >= 0 ? rest.Substring(0, hash) : rest).Trim();
                }
                result.Entries.Add(new Entry { Key = key, Value = value, Line = lineNumber });
            }
            return result;
        }

        private static int FindClose(string s, char quote)
        {
            for (int k = 0; k < s.Length; k++)
            {
                if (quote == '"' && s[k] == '\\') { k++; continue; }
                if (s[k] == quote) return k;
            }
            return -1;
        }

        private static int IndexOfComment(string s)
        {
            for (int k = 0; k < s.Length; k++)
                if (s[k] == '#' && (k == 0 || char.IsWhiteSpace(s[k - 1]))) return k;
            return -1;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int k = 0; k < s.Length; k++)
            {
                char c = s[k];
                if (c != '\\' || k + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++k];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append('\\').Append(n); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>One <c>KEY=value</c> line that <see cref="Parse"/> reads back as the same value: bare when that is safe, double-quoted and escaped otherwise.</summary>
        public static string Format(string key, string value)
        {
            value = value ?? "";
            bool bare = true;
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c) || "_-./:@%+,=~^".IndexOf(c) >= 0) continue;
                bare = false;
                break;
            }
            if (bare) return key + "=" + value;
            var sb = new StringBuilder(key.Length + value.Length + 4);
            sb.Append(key).Append("=\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>A whole file: <paramref name="header"/> lines as comments, then one line per variable.</summary>
        public static string Write(IEnumerable<KeyValuePair<string, string>> vars, IEnumerable<string> header = null)
        {
            var sb = new StringBuilder();
            if (header != null) foreach (var h in header) sb.Append("# ").Append(h).Append('\n');
            foreach (var kv in vars) sb.Append(Format(kv.Key, kv.Value)).Append('\n');
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------------ names

        /// <summary>A name the environment can carry: <c>^[A-Za-z_][A-Za-z0-9_]*$</c>.</summary>
        public static bool IsValidKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            for (int k = 0; k < key.Length; k++)
            {
                char c = key[k];
                bool ok = c == '_' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (k > 0 && c >= '0' && c <= '9');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// Names Nebula sets itself, which a game cannot set for its workers: <c>PORT</c> (the port a worker listens on,
        /// set by the host) and anything starting with <c>NEBULA_</c>. Compared without regard to case.
        /// </summary>
        public static bool IsReserved(string key) =>
            !string.IsNullOrEmpty(key) && (string.Equals(key, "PORT", StringComparison.OrdinalIgnoreCase) || key.StartsWith("NEBULA_", StringComparison.OrdinalIgnoreCase));

        /// <summary>Why <paramref name="key"/> cannot be a game variable, or null when it can.</summary>
        public static string KeyProblem(string key)
        {
            if (!IsValidKey(key)) return $"'{Printable(key)}' is not a valid variable name: use letters, digits and _, and do not start with a digit";
            if (string.Equals(key, "PORT", StringComparison.OrdinalIgnoreCase)) return $"{key} is reserved: Nebula sets PORT to the port each worker listens on";
            if (IsReserved(key)) return $"{key} is reserved: names starting with NEBULA_ are Nebula's own settings";
            return null;
        }

        /// <summary>Why <paramref name="value"/> cannot be a variable's value, or null when it can.</summary>
        public static string ValueProblem(string key, string value)
        {
            if (value == null) return null;
            if (value.IndexOf('\0') >= 0) return $"the value of {key} contains a NUL character";
            int bytes = Encoding.UTF8.GetByteCount(value);
            if (bytes > MaxValueBytes) return $"the value of {key} is {bytes} bytes; the limit is {MaxValueBytes} (32 KiB)";
            return null;
        }

        private static string Printable(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsControl(c) ? '?' : c);
            return sb.Length > 40 ? sb.ToString(0, 40) + "..." : sb.ToString();
        }
    }
}
