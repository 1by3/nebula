using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// The wire and save format of <see cref="NetworkMapBase"/> contents (docs/replicated-collections.md D3). Keys and
    /// values are opaque, length-prefixed bytes, so a party with no game types (the gateway) can keep a map's
    /// contents and hand a late joiner a full copy.
    /// <code>
    /// body    = [version:byte=1][flags:byte (1 = clear first)][count:int] { [op:byte (0 remove, 1 set)][keyLen:ushort][key]([valueLen:ushort][value]) }
    /// section = [mapCount:byte] { [mapIndex:byte][bodyLength:int][body] }
    /// </code>
    /// A full copy is a body with the clear flag set and every entry as a set. A delta is a body with only the keys
    /// that changed, each with its final state, so applying it to a copy that already contains it changes nothing.
    /// </summary>
    public static class NetworkMapCodec
    {
        public const byte Version = 1;
        public const byte FlagClear = 1;
        public const byte OpRemove = 0;
        public const byte OpSet = 1;
        /// <summary>Bytes of a body with no entries.</summary>
        public const int HeaderBytes = 6;

        /// <summary>The encoded size of one entry.</summary>
        public static int EntryBytes(int keyLength, int valueLength, bool set) => 1 + 2 + keyLength + (set ? 2 + valueLength : 0);

        /// <summary>Begin a body; returns where the count goes. Finish it with <see cref="EndBody"/>.</summary>
        public static int BeginBody(NetworkWriter w, bool clear)
        {
            w.WriteByte(Version);
            w.WriteByte(clear ? FlagClear : (byte)0);
            int at = w.Length;
            w.WriteInt(0);
            return at;
        }

        public static void EndBody(NetworkWriter w, int countAt, int count) => w.PatchInt(countAt, count);

        public static void WriteSet(NetworkWriter w, ArraySegment<byte> key, ArraySegment<byte> value)
        {
            w.WriteByte(OpSet);
            w.WriteBytes(key);
            w.WriteBytes(value);
        }

        public static void WriteRemove(NetworkWriter w, ArraySegment<byte> key)
        {
            w.WriteByte(OpRemove);
            w.WriteBytes(key);
        }

        /// <summary>One entry of a body as <see cref="ReadBody"/> hands it over. The segments point into the body.</summary>
        public readonly struct Entry
        {
            public readonly bool IsSet;
            public readonly ArraySegment<byte> Key;
            public readonly ArraySegment<byte> Value;
            public Entry(bool isSet, ArraySegment<byte> key, ArraySegment<byte> value) { IsSet = isSet; Key = key; Value = value; }
        }

        /// <summary>
        /// Read a body: <paramref name="clear"/> says whether the receiver empties its copy first, and each entry is
        /// added to <paramref name="into"/> in order. Throws <see cref="FormatException"/> on a newer version.
        /// </summary>
        public static void ReadBody(NetworkReader r, out bool clear, List<Entry> into)
        {
            byte version = r.ReadByte();
            if (version > Version) throw new FormatException($"map encoded by a newer build (version {version}, this build reads {Version})");
            byte flags = r.ReadByte();
            clear = (flags & FlagClear) != 0;
            int count = r.ReadInt();
            if (count < 0) throw new FormatException("negative entry count");
            for (int i = 0; i < count; i++)
            {
                byte op = r.ReadByte();
                var key = r.ReadBytesSegment();
                var value = op == OpSet ? r.ReadBytesSegment() : default;
                into.Add(new Entry(op == OpSet, key, value));
            }
        }

        /// <summary>Begin a section; returns where the map count goes.</summary>
        public static int BeginSection(NetworkWriter w)
        {
            int at = w.Length;
            w.WriteByte(0);
            return at;
        }

        /// <summary>Begin one map of a section; returns where its body length goes. Finish it with <see cref="EndMap"/>.</summary>
        public static int BeginMap(NetworkWriter w, byte mapIndex)
        {
            w.WriteByte(mapIndex);
            int at = w.Length;
            w.WriteInt(0);
            return at;
        }

        public static void EndMap(NetworkWriter w, int lengthAt) => w.PatchInt(lengthAt, w.Length - lengthAt - 4);

        public static void EndSection(NetworkWriter w, int countAt, byte count) => w.PatchByte(countAt, count);

        /// <summary>Walk a section: each map's index and body.</summary>
        public static void ReadSection(ArraySegment<byte> section, Action<byte, ArraySegment<byte>> onMap)
        {
            if (section.Array == null || section.Count == 0) return;
            var r = new NetworkReader(section);
            int count = r.ReadByte();
            for (int i = 0; i < count; i++)
            {
                byte index = r.ReadByte();
                int length = r.ReadInt();
                onMap(index, r.ReadSegment(length));
            }
        }
    }

    /// <summary>Compares byte arrays by content: how a party without game types keys a map (the gateway's cache).</summary>
    public sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new ByteArrayComparer();

        public bool Equals(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public int GetHashCode(byte[] a)
        {
            if (a == null) return 0;
            unchecked
            {
                int h = (int)2166136261;
                for (int i = 0; i < a.Length; i++) h = (h ^ a[i]) * 16777619;
                return h;
            }
        }
    }

    /// <summary>
    /// The contents of an entity's maps kept as opaque bytes, by a party that relays them without knowing their types:
    /// the gateway, so a late joiner is sent the current contents (docs/replicated-collections.md D6). Seeded from a
    /// full section, updated by delta sections, encoded back into a full section on demand (once per change).
    /// </summary>
    public sealed class NetworkMapCache
    {
        private readonly Dictionary<byte, Dictionary<byte[], byte[]>> _maps = new Dictionary<byte, Dictionary<byte[], byte[]>>();
        private readonly List<NetworkMapCodec.Entry> _entries = new List<NetworkMapCodec.Entry>();
        private byte[] _encoded;

        /// <summary>How many entries the map at <paramref name="mapIndex"/> holds; 0 when unknown.</summary>
        public int CountOf(byte mapIndex) => _maps.TryGetValue(mapIndex, out var m) ? m.Count : 0;

        /// <summary>The value bytes held for a key, or null.</summary>
        public byte[] Get(byte mapIndex, byte[] key) => _maps.TryGetValue(mapIndex, out var m) && m.TryGetValue(key, out var v) ? v : null;

        /// <summary>Forget everything and take <paramref name="section"/> (a full copy, or null for no maps).</summary>
        public void Reset(byte[] section)
        {
            _maps.Clear();
            _encoded = null;
            if (section != null && section.Length > 0)
            {
                Apply(new ArraySegment<byte>(section));
                _encoded = section;
            }
            else _encoded = Array.Empty<byte>();
        }

        /// <summary>Apply a section, full or delta.</summary>
        public void Apply(ArraySegment<byte> section)
        {
            _encoded = null;
            NetworkMapCodec.ReadSection(section, (index, body) =>
            {
                if (!_maps.TryGetValue(index, out var map)) _maps[index] = map = new Dictionary<byte[], byte[]>(ByteArrayComparer.Instance);
                _entries.Clear();
                NetworkMapCodec.ReadBody(new NetworkReader(body), out bool clear, _entries);
                if (clear) map.Clear();
                for (int i = 0; i < _entries.Count; i++)
                {
                    var e = _entries[i];
                    var key = Copy(e.Key);
                    if (e.IsSet) map[key] = Copy(e.Value);
                    else map.Remove(key);
                }
                _entries.Clear();
            });
        }

        /// <summary>The contents as a full section; empty when there are no maps.</summary>
        public byte[] Encode()
        {
            if (_encoded != null) return _encoded;
            if (_maps.Count == 0) return _encoded = Array.Empty<byte>();
            var w = new NetworkWriter(256);
            int countAt = NetworkMapCodec.BeginSection(w);
            byte n = 0;
            foreach (var kv in _maps)
            {
                int lengthAt = NetworkMapCodec.BeginMap(w, kv.Key);
                int bodyAt = NetworkMapCodec.BeginBody(w, clear: true);
                foreach (var entry in kv.Value) NetworkMapCodec.WriteSet(w, new ArraySegment<byte>(entry.Key), new ArraySegment<byte>(entry.Value));
                NetworkMapCodec.EndBody(w, bodyAt, kv.Value.Count);
                NetworkMapCodec.EndMap(w, lengthAt);
                n++;
            }
            NetworkMapCodec.EndSection(w, countAt, n);
            return _encoded = w.ToArray();
        }

        /// <summary>A full section with a delta section applied to it: for a spawn that is held and will not be sent again.</summary>
        public static byte[] Fold(byte[] full, byte[] delta)
        {
            var cache = new NetworkMapCache();
            cache.Reset(full);
            if (delta != null && delta.Length > 0) cache.Apply(new ArraySegment<byte>(delta));
            var encoded = cache.Encode();
            return encoded.Length > 0 ? encoded : null;
        }

        private static byte[] Copy(ArraySegment<byte> s)
        {
            var a = new byte[s.Count];
            if (s.Count > 0) Buffer.BlockCopy(s.Array, s.Offset, a, 0, s.Count);
            return a;
        }
    }

    /// <summary>
    /// Changed map entries for one entity (docs/replicated-collections.md D4): <see cref="MsgId.EntityMaps"/> worker
    /// to gateway to client, <see cref="MsgId.GhostMaps"/> worker to worker. <see cref="Maps"/> is a section of the
    /// maps that changed, each body a delta.
    /// </summary>
    public struct EntityMapsMsg
    {
        public ulong NetId;
        public uint Epoch;
        public byte[] Maps;

        public void Write(NetworkWriter w, MsgId id)
        {
            w.WriteByte((byte)id);
            w.WriteULong(NetId);
            w.WriteUInt(Epoch);
            var maps = Maps ?? Array.Empty<byte>();
            w.WriteInt(maps.Length);
            w.WriteRaw(new ArraySegment<byte>(maps));
        }

        public static EntityMapsMsg Read(NetworkReader r)
        {
            var m = new EntityMapsMsg { NetId = r.ReadULong(), Epoch = r.ReadUInt() };
            var seg = r.ReadSegment(r.ReadInt());
            m.Maps = new byte[seg.Count];
            if (seg.Count > 0) Buffer.BlockCopy(seg.Array, seg.Offset, m.Maps, 0, seg.Count);
            return m;
        }
    }
}
