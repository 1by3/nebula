using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Nebula
{
    /// <summary>
    /// Claims from a player's verified sign-in token that the gateway forwards to workers
    /// (<see cref="NebulaConfig.ForwardedClaims"/>): the claim names the game listed, with their values as strings.
    /// Workers read them through <see cref="NetworkIdentity.OwnerClaims"/>, <see cref="PlayerInfo.Claims"/> and
    /// <see cref="NebulaWorker.GetPlayerClaims"/>. This class holds the limits, the selection from a token and the
    /// wire codec.
    /// <para>
    /// Only claims from a token the gateway verified against a trusted OpenID provider are forwarded. Anonymous
    /// players, whose tokens the gateway issued itself, have none. Claims never reach clients.
    /// </para>
    /// </summary>
    public static class PlayerClaims
    {
        /// <summary>Most claims forwarded for one player. Names past this in <see cref="NebulaConfig.ForwardedClaims"/> are ignored.</summary>
        public const int MaxCount = 16;
        /// <summary>Longest claim value forwarded, in characters. A longer value is dropped, not cut short.</summary>
        public const int MaxValueLength = 256;
        /// <summary>Longest claim name accepted, in characters.</summary>
        public const int MaxNameLength = 64;

        /// <summary>The claims of a player who has none: an anonymous player, a bot, or an entity no player owns.</summary>
        public static readonly IReadOnlyDictionary<string, string> Empty = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

        /// <summary>
        /// Split a comma- or space-separated list of claim names (<see cref="NebulaConfig.ForwardedClaims"/>).
        /// Duplicates, names longer than <see cref="MaxNameLength"/> and names past <see cref="MaxCount"/> are left out.
        /// </summary>
        public static List<string> ParseNames(string spec)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(spec)) return names;
            foreach (var part in spec.Split(new[] { ',', ';', ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = part.Trim();
                if (name.Length == 0 || name.Length > MaxNameLength || names.Contains(name)) continue;
                if (names.Count == MaxCount) break;
                names.Add(name);
            }
            return names;
        }

        /// <summary>
        /// The claims named in <paramref name="names"/>, from a verified token's <paramref name="payload"/>. Strings are
        /// copied as they are; numbers and booleans are formatted the way <see cref="JsonWebToken.Claim"/> formats them.
        /// A claim that is missing, null, an array or an object, or longer than <see cref="MaxValueLength"/>, is left
        /// out. <paramref name="dropped"/> lists the names that were present but left out, for a log line.
        /// </summary>
        public static IReadOnlyDictionary<string, string> Select(Dictionary<string, object> payload, IReadOnlyList<string> names, List<string> dropped = null)
        {
            if (payload == null || names == null || names.Count == 0) return Empty;
            Dictionary<string, string> selected = null;
            for (int i = 0; i < names.Count && i < MaxCount; i++)
            {
                string name = names[i];
                if (string.IsNullOrEmpty(name) || !payload.ContainsKey(name)) continue;
                string value = JsonWebToken.Claim(payload, name);
                if (value == null || value.Length > MaxValueLength) { dropped?.Add(name); continue; }
                selected ??= new Dictionary<string, string>(StringComparer.Ordinal);
                selected[name] = value;
            }
            return selected == null ? Empty : new ReadOnlyDictionary<string, string>(selected);
        }

        /// <summary>A read-only copy of <paramref name="claims"/> that respects the limits; for tests and hand-built player infos.</summary>
        public static IReadOnlyDictionary<string, string> From(IEnumerable<KeyValuePair<string, string>> claims)
        {
            if (claims == null) return Empty;
            Dictionary<string, string> copy = null;
            foreach (var kv in claims)
            {
                if (!Acceptable(kv.Key, kv.Value)) continue;
                copy ??= new Dictionary<string, string>(StringComparer.Ordinal);
                if (copy.Count == MaxCount && !copy.ContainsKey(kv.Key)) break;
                copy[kv.Key] = kv.Value;
            }
            return copy == null ? Empty : new ReadOnlyDictionary<string, string>(copy);
        }

        /// <summary>Write <paramref name="claims"/> (null = none): a count byte, then name and value strings.</summary>
        public static void Write(NetworkWriter w, IReadOnlyDictionary<string, string> claims)
        {
            int count = 0;
            if (claims != null)
                foreach (var kv in claims) if (Acceptable(kv.Key, kv.Value)) count++;
            if (count > MaxCount) count = MaxCount;
            w.WriteByte((byte)count);
            if (count == 0) return;
            int written = 0;
            foreach (var kv in claims)
            {
                if (written == count) break;
                if (!Acceptable(kv.Key, kv.Value)) continue;
                w.WriteString(kv.Key);
                w.WriteString(kv.Value);
                written++;
            }
        }

        /// <summary>Read what <see cref="Write"/> wrote. A section breaking the limits throws, like any other malformed message.</summary>
        public static IReadOnlyDictionary<string, string> Read(NetworkReader r)
        {
            int count = r.ReadByte();
            if (count == 0) return Empty;
            if (count > MaxCount) throw new FormatException($"{count} player claims is more than the {MaxCount} allowed");
            var claims = new Dictionary<string, string>(count, StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string name = r.ReadString() ?? "";
                string value = r.ReadString() ?? "";
                if (!Acceptable(name, value)) throw new FormatException("a player claim breaks the name or value limits");
                claims[name] = value;
            }
            return new ReadOnlyDictionary<string, string>(claims);
        }

        /// <summary>True when both dictionaries hold the same names and values.</summary>
        public static bool SameAs(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        {
            int ca = a?.Count ?? 0, cb = b?.Count ?? 0;
            if (ca != cb) return false;
            if (ca == 0) return true;
            foreach (var kv in a)
                if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
            return true;
        }

        private static bool Acceptable(string name, string value) =>
            !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && value != null && value.Length <= MaxValueLength;
    }
}
