using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Nebula
{
    /// <summary>
    /// What one virtual player knows about the others of the Multiplayer Play Mode window, read from the project's
    /// <c>Library/VP/SystemData.json</c> (<see cref="EditorRosterReader"/>). <see cref="EditorRunPlan.Resolve(NebulaEditorRunMode, bool, bool, bool, IReadOnlyList{string}, bool, EditorRoster)"/>
    /// uses it so that exactly one virtual player hosts the server: an untagged virtual player hosts only when it is
    /// the active one with the lowest index, and every other one is a client.
    /// </summary>
    public readonly struct EditorRoster
    {
        /// <summary>Whether this player was found in the roster. When false the other facts mean nothing and the plan keeps the older rule: an untagged virtual player hosts.</summary>
        public bool Known { get; }
        /// <summary>Whether any active player carries <see cref="EditorRunPlan.ServerTag"/>, which makes every untagged virtual player a client.</summary>
        public bool AnyServerTagged { get; }
        /// <summary>Whether another active player with <see cref="EditorRunPlan.ServerTag"/> has a lower index than this one.</summary>
        public bool LowerIndexServerTagged { get; }
        /// <summary>Whether no other active virtual player without <see cref="EditorRunPlan.ClientTag"/> has a lower index than this one.</summary>
        public bool IsFirstHostCandidate { get; }

        public EditorRoster(bool anyServerTagged, bool lowerIndexServerTagged, bool isFirstHostCandidate)
        {
            Known = true;
            AnyServerTagged = anyServerTagged;
            LowerIndexServerTagged = lowerIndexServerTagged;
            IsFirstHostCandidate = isFirstHostCandidate;
        }

        /// <summary>No roster: the older rule applies.</summary>
        public static EditorRoster Unknown => default;

        /// <summary>
        /// Work out the roster facts for the virtual player that runs from <c>Library/VP/&lt;playerFolder&gt;</c>
        /// (the folder is the player's <c>m_Prefix</c> followed by its <c>m_Id</c>) from the text of
        /// <c>SystemData.json</c>. Tolerant: anything unreadable gives <see cref="Unknown"/> and a reason in
        /// <paramref name="problem"/>; null when the roster was read.
        /// </summary>
        public static EditorRoster Parse(string json, string playerFolder, out string problem)
        {
            problem = null;
            if (string.IsNullOrEmpty(playerFolder)) { problem = "this is not a virtual player's folder"; return Unknown; }
            object root = MiniJson.Parse(json);
            if (!(root is Dictionary<string, object> top) || !top.TryGetValue("Data", out var dataObj) || !(dataObj is Dictionary<string, object> data))
            {
                problem = "SystemData.json has no player list";
                return Unknown;
            }
            var players = new List<Entry>();
            Entry me = null;
            foreach (var pair in data)
            {
                if (!(pair.Value is Dictionary<string, object> p)) continue;
                var e = new Entry
                {
                    Index = ReadIndex(p, pair.Key),
                    Active = !(p.TryGetValue("Active", out var a) && a is bool b) || b,
                    IsMain = p.TryGetValue("Type", out var t) && t is double td && td == 0d,
                    Folder = ReadFolder(p),
                    Tags = ReadTags(p),
                };
                players.Add(e);
                if (e.Folder != null && string.Equals(e.Folder, playerFolder, StringComparison.OrdinalIgnoreCase)) me = e;
            }
            if (me == null)
            {
                problem = $"{playerFolder} is not listed in SystemData.json";
                return Unknown;
            }
            bool anyServer = false, lowerServer = false, firstCandidate = !HasTag(me.Tags, EditorRunPlan.ClientTag);
            foreach (var e in players)
            {
                bool isMe = ReferenceEquals(e, me);
                if (!isMe && !e.Active) continue;
                if (HasTag(e.Tags, EditorRunPlan.ServerTag))
                {
                    anyServer = true;
                    if (!isMe && e.Index < me.Index) lowerServer = true;
                }
                if (!isMe && !e.IsMain && e.Index < me.Index && !HasTag(e.Tags, EditorRunPlan.ClientTag)) firstCandidate = false;
            }
            return new EditorRoster(anyServer, lowerServer, firstCandidate);
        }

        private sealed class Entry
        {
            public int Index;
            public bool Active;
            public bool IsMain;
            public string Folder;
            public List<string> Tags;
        }

        private static int ReadIndex(Dictionary<string, object> p, string key)
        {
            if (p.TryGetValue("Index", out var i) && i is double d) return (int)d;
            return int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) ? k : int.MaxValue;
        }

        private static string ReadFolder(Dictionary<string, object> p)
        {
            if (p.TryGetValue("TypeDependentPlayerInfo", out var info) && info is Dictionary<string, object> di
                && di.TryGetValue("VirtualProjectIdentifier", out var vpi) && vpi is Dictionary<string, object> id
                && id.TryGetValue("m_Id", out var mid) && mid is string sid && sid.Length > 0)
                return (id.TryGetValue("m_Prefix", out var pre) && pre is string sp ? sp : "") + sid;
            return null;
        }

        private static List<string> ReadTags(Dictionary<string, object> p)
        {
            var tags = new List<string>();
            if (p.TryGetValue("Tags", out var t) && t is List<object> list)
                foreach (var item in list)
                {
                    if (item is string s) tags.Add(s);
                    else if (item is Dictionary<string, object> o && o.TryGetValue("Name", out var n) && n is string sn) tags.Add(sn);
                }
            return tags;
        }

        private static bool HasTag(IReadOnlyList<string> tags, string tag)
        {
            for (int i = 0; i < tags.Count; i++)
                if (string.Equals(tags[i]?.Trim(), tag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>Reads <c>Library/VP/SystemData.json</c>, where Multiplayer Play Mode lists its players.</summary>
    public static class EditorRosterReader
    {
        /// <summary>The roster file: <c>&lt;root&gt;/Library/VP/SystemData.json</c>.</summary>
        public static string RosterFile(string projectRoot) => System.IO.Path.Combine(projectRoot, "Library", "VP", "SystemData.json");

        /// <summary>Read the roster for the virtual player in <paramref name="playerFolder"/>; <see cref="EditorRoster.Unknown"/> with a reason in <paramref name="problem"/> when it cannot be read.</summary>
        public static EditorRoster Read(string projectRoot, string playerFolder, out string problem)
        {
            string path = RosterFile(projectRoot);
            string text;
            try
            {
                if (!System.IO.File.Exists(path)) { problem = "Library/VP/SystemData.json does not exist"; return EditorRoster.Unknown; }
                // The Editor keeps the file open for writing; share it.
                using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                    text = reader.ReadToEnd();
            }
            catch (Exception e)
            {
                problem = $"Library/VP/SystemData.json could not be read ({e.GetType().Name})";
                return EditorRoster.Unknown;
            }
            return EditorRoster.Parse(text, playerFolder, out problem);
        }
    }

    /// <summary>A small JSON reader for <see cref="EditorRoster"/>: objects, arrays, strings, numbers (as double), booleans and null. Returns null for anything malformed.</summary>
    internal static class MiniJson
    {
        public static object Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                int i = 0;
                object value = ReadValue(json, ref i, 0);
                SkipSpace(json, ref i);
                return i == json.Length ? value : null;
            }
            catch (FormatException) { return null; }
        }

        private static void SkipSpace(string s, ref int i)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == (char)0xFEFF)) i++;
        }

        private static object ReadValue(string s, ref int i, int depth)
        {
            if (depth > 32) throw new FormatException();
            SkipSpace(s, ref i);
            if (i >= s.Length) throw new FormatException();
            char c = s[i];
            if (c == '{')
            {
                i++;
                var map = new Dictionary<string, object>();
                SkipSpace(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return map; }
                while (true)
                {
                    SkipSpace(s, ref i);
                    string key = ReadString(s, ref i);
                    SkipSpace(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException();
                    i++;
                    map[key] = ReadValue(s, ref i, depth + 1);
                    SkipSpace(s, ref i);
                    if (i >= s.Length) throw new FormatException();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return map; }
                    throw new FormatException();
                }
            }
            if (c == '[')
            {
                i++;
                var list = new List<object>();
                SkipSpace(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(ReadValue(s, ref i, depth + 1));
                    SkipSpace(s, ref i);
                    if (i >= s.Length) throw new FormatException();
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return list; }
                    throw new FormatException();
                }
            }
            if (c == '"') return ReadString(s, ref i);
            if (Literal(s, ref i, "true")) return true;
            if (Literal(s, ref i, "false")) return false;
            if (Literal(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) throw new FormatException();
            return number;
        }

        private static bool Literal(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        private static string ReadString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException();
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length || !int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code)) throw new FormatException();
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException();
        }
    }
}
