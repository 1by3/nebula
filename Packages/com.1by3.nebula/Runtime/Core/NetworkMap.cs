using System;
using System.Collections;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>What happened to one key of a <see cref="NetworkMap{TKey, TValue}"/>.</summary>
    public enum NetworkMapChangeKind : byte
    {
        /// <summary>The key was not in the map and now is.</summary>
        Added = 0,
        /// <summary>The key was in the map and its value changed.</summary>
        Updated = 1,
        /// <summary>The key left the map (removed, or cleared).</summary>
        Removed = 2,
    }

    /// <summary>One key's change, raised by <see cref="NetworkMap{TKey, TValue}.OnChanged"/> on every copy of the map.</summary>
    public readonly struct NetworkMapChange<TKey, TValue>
    {
        public readonly NetworkMapChangeKind Kind;
        public readonly TKey Key;
        /// <summary>The value before the change; default for <see cref="NetworkMapChangeKind.Added"/>.</summary>
        public readonly TValue OldValue;
        /// <summary>The value after the change; default for <see cref="NetworkMapChangeKind.Removed"/>.</summary>
        public readonly TValue NewValue;

        public NetworkMapChange(NetworkMapChangeKind kind, TKey key, TValue oldValue, TValue newValue)
        {
            Kind = kind; Key = key; OldValue = oldValue; NewValue = newValue;
        }

        public override string ToString() => $"{Kind} {Key}";
    }

    /// <summary>
    /// The untyped half of <see cref="NetworkMap{TKey, TValue}"/>: what <see cref="NetworkIdentity"/> needs to send
    /// and apply a map without knowing its types (docs/replicated-collections.md). <see cref="NetworkVariableBase.Write"/>
    /// and <see cref="NetworkVariableBase.Read"/> are the full contents, which is what persistence stores.
    /// </summary>
    public abstract class NetworkMapBase : NetworkVariableBase
    {
        /// <summary>
        /// The most a map may hold by default, encoded (61,440 bytes of body, D11): it keeps a
        /// <see cref="PersistAttribute"/> map inside one persisted entry and a full copy a sane size for a spawn.
        /// </summary>
        public const int MaxEncodedBytes = 60 * 1024;

        /// <summary>
        /// The most this map may hold, encoded: <see cref="MaxEncodedBytes"/> unless the constructor chose otherwise.
        /// A <see cref="PersistAttribute"/> map larger than 65,535 bytes cannot be saved as one entry.
        /// </summary>
        public int Capacity { get; protected set; } = MaxEncodedBytes;

        /// <summary>This map's position among its entity's maps (<see cref="NetworkIdentity.Maps"/>): its index on the wire.</summary>
        internal byte MapIndex;

        /// <summary>How many entries the map holds.</summary>
        public abstract int Count { get; }

        /// <summary>The encoded size of the full contents, in bytes. At most <see cref="Capacity"/>.</summary>
        public abstract int EncodedBytes { get; }

        /// <summary>Write the keys changed since the last send, as a delta body. Only called when <see cref="NetworkVariableBase.Dirty"/>.</summary>
        internal abstract void WriteDelta(NetworkWriter writer);

        /// <summary>Write the full contents as a body.</summary>
        internal abstract void WriteFull(NetworkWriter writer);

        /// <summary>Apply a body (full or delta) received from the authority, raising a change for each key that differs.</summary>
        internal abstract void ApplyBody(NetworkReader reader);

        /// <summary>Forget what changed since the last send: it has been sent, or this copy is not the authority.</summary>
        internal abstract void ClearChanges();

        /// <summary>The next send is the full contents (clear first, then every entry): after a restore onto a live entity.</summary>
        internal abstract void MarkResendAll();

        /// <inheritdoc/>
        public override void Write(NetworkWriter writer) => WriteFull(writer);

        /// <inheritdoc/>
        public override void Read(NetworkReader reader) => ApplyBody(reader);
    }

    /// <summary>
    /// A keyed collection replicated from the authoritative worker to clients and ghost workers, sending only what
    /// changed. Declare it on a <see cref="NetworkBehaviour"/> like a <see cref="NetworkVariable{T}"/>, and add
    /// <see cref="PersistAttribute"/> to save it:
    /// <code>
    /// [Persist] public NetworkMap&lt;int, ItemStack&gt; Slots = new NetworkMap&lt;int, ItemStack&gt;();
    /// </code>
    /// Only the authority may write it. Each tick, the keys that changed go out as one reliable message; a client that
    /// arrives later is given the current contents by its gateway, then the same changes as everyone else. Keys and
    /// values use <see cref="NetworkSerialization"/>, so any type a <see cref="NetworkVariable{T}"/> can hold works.
    /// A map is capped at <see cref="NetworkMapBase.Capacity"/> encoded bytes (60 KB by default).
    /// </summary>
    public sealed class NetworkMap<TKey, TValue> : NetworkMapBase, IReadOnlyDictionary<TKey, TValue>
    {
        private readonly Dictionary<TKey, TValue> _map;
        /// <summary>Encoded key and value sizes per key, so the size cap is kept without re-encoding the map.</summary>
        private readonly Dictionary<TKey, int> _sizes;
        /// <summary>Keys changed since the last send (authority).</summary>
        private readonly HashSet<TKey> _touched;
        private bool _cleared;
        private int _encodedBytes = NetworkMapCodec.HeaderBytes;

        private static readonly NetworkWriter KeyScratch = new NetworkWriter(64);
        private static readonly NetworkWriter ValueScratch = new NetworkWriter(256);
        private static readonly NetworkReader EntryReader = new NetworkReader(Array.Empty<byte>());
        private static readonly List<NetworkMapCodec.Entry> Entries = new List<NetworkMapCodec.Entry>();
        private static readonly IEqualityComparer<TValue> ValueComparer = EqualityComparer<TValue>.Default;

        public NetworkMap() : this(null) { }

        /// <param name="comparer">How keys are compared; the default comparer of <typeparamref name="TKey"/> when null.</param>
        /// <param name="capacityBytes">
        /// The most the map may hold, encoded (<see cref="NetworkMapBase.Capacity"/>); 0 for
        /// <see cref="NetworkMapBase.MaxEncodedBytes"/>.
        /// </param>
        public NetworkMap(IEqualityComparer<TKey> comparer, int capacityBytes = 0)
        {
            if (capacityBytes < 0) throw new ArgumentOutOfRangeException(nameof(capacityBytes));
            if (capacityBytes > 0) Capacity = capacityBytes;
            _map = new Dictionary<TKey, TValue>(comparer);
            _sizes = new Dictionary<TKey, int>(comparer);
            _touched = new HashSet<TKey>(comparer);
        }

        /// <summary>
        /// Raised on every copy (the authority as it writes, others as they apply what it sent) once per key that was
        /// added, updated or removed.
        /// </summary>
        public event Action<NetworkMapChange<TKey, TValue>> OnChanged;

        public override int Count => _map.Count;
        public override int EncodedBytes => _encodedBytes;
        public IEnumerable<TKey> Keys => _map.Keys;
        public IEnumerable<TValue> Values => _map.Values;
        public bool ContainsKey(TKey key) => _map.ContainsKey(key);
        public bool TryGetValue(TKey key, out TValue value) => _map.TryGetValue(key, out value);
        public Dictionary<TKey, TValue>.Enumerator GetEnumerator() => _map.GetEnumerator();
        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => _map.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _map.GetEnumerator();

        /// <summary>
        /// Get a value, or (authority) add or replace one. Setting a value equal to the current one does nothing.
        /// Throws <see cref="InvalidOperationException"/> when the map would pass its size cap.
        /// </summary>
        public TValue this[TKey key]
        {
            get => _map[key];
            set
            {
                if (!TrySet(key, value)) throw new InvalidOperationException(CapMessage(key));
            }
        }

        /// <summary>Add a key that is not in the map (authority). Throws <see cref="ArgumentException"/> if it is.</summary>
        public void Add(TKey key, TValue value)
        {
            if (_map.ContainsKey(key)) throw new ArgumentException($"key {key} is already in the map", nameof(key));
            this[key] = value;
        }

        /// <summary>
        /// Add or replace a value (authority). Returns false, and changes nothing, when the map would pass
        /// <see cref="NetworkMapBase.Capacity"/>; true otherwise, including when the write was ignored because
        /// this copy may not write.
        /// </summary>
        public bool TrySet(TKey key, TValue value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (!MayWrite()) return true;
            bool had = _map.TryGetValue(key, out var old);
            if (had && ValueComparer.Equals(old, value)) return true;
            int size = EntrySize(key, value);
            int next = _encodedBytes + size - (had ? _sizes[key] : 0);
            if (next > Capacity) return false;
            _map[key] = value;
            _sizes[key] = size;
            _encodedBytes = next;
            Touch(key);
            OnChanged?.Invoke(new NetworkMapChange<TKey, TValue>(had ? NetworkMapChangeKind.Updated : NetworkMapChangeKind.Added, key, old, value));
            return true;
        }

        /// <summary>Remove a key (authority). Returns whether it was there.</summary>
        public bool Remove(TKey key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (!MayWrite()) return false;
            if (!_map.TryGetValue(key, out var old)) return false;
            _map.Remove(key);
            _encodedBytes -= _sizes[key];
            _sizes.Remove(key);
            Touch(key);
            OnChanged?.Invoke(new NetworkMapChange<TKey, TValue>(NetworkMapChangeKind.Removed, key, old, default));
            return true;
        }

        /// <summary>Remove every key (authority). Raises <see cref="NetworkMapChangeKind.Removed"/> for each.</summary>
        public void Clear()
        {
            if (!MayWrite() || (_map.Count == 0 && !_cleared && _touched.Count == 0)) return;
            var removed = OnChanged != null && _map.Count > 0 ? new List<KeyValuePair<TKey, TValue>>(_map) : null;
            _map.Clear();
            _sizes.Clear();
            _encodedBytes = NetworkMapCodec.HeaderBytes;
            _touched.Clear();
            _cleared = true;
            MarkChanged();
            if (removed == null) return;
            foreach (var kv in removed) OnChanged?.Invoke(new NetworkMapChange<TKey, TValue>(NetworkMapChangeKind.Removed, kv.Key, kv.Value, default));
        }

        /// <summary>
        /// Send the current value of <paramref name="key"/> again (authority): for a reference-typed value that was
        /// changed in place, which the map cannot see. Re-measures it against the size cap; returns false, sending
        /// nothing, when it no longer fits.
        /// </summary>
        public bool SetDirty(TKey key)
        {
            if (!MayWrite() || !_map.TryGetValue(key, out var value)) return false;
            int size = EntrySize(key, value);
            int next = _encodedBytes + size - _sizes[key];
            if (next > Capacity) return false;
            _sizes[key] = size;
            _encodedBytes = next;
            Touch(key);
            return true;
        }

        // ------------------------------------------------------------------------------------------ authority

        private bool MayWrite()
        {
            var identity = Owner != null ? Owner.Identity : null;
            if (identity != null && identity.IsSpawned && !Owner.HasAuthority && !identity.ReceivingHandover)
            {
                NebulaLog.Warn($"NetworkMap {Name} on {Owner.GetType().Name} written without authority (netId {Owner.NetId}); ignored");
                return false;
            }
            return true;
        }

        private void Touch(TKey key)
        {
            _touched.Add(key);
            MarkChanged();
        }

        private void MarkChanged()
        {
            Dirty = true;
            var identity = Owner != null ? Owner.Identity : null;
            identity?.MarkMapsDirty();
            if (!Persist) return;
            PersistDirty = true;
            identity?.Persistent?.MarkDirty();
        }

        internal override void ClearChanges()
        {
            _touched.Clear();
            _cleared = false;
            Dirty = false;
        }

        internal override void MarkResendAll()
        {
            _touched.Clear();
            foreach (var k in _map.Keys) _touched.Add(k);
            _cleared = true;
            MarkChanged();
        }

        private string CapMessage(TKey key) =>
            $"NetworkMap {Name}: setting key {key} would take the map past {Capacity} encoded bytes; nothing changed";

        // ------------------------------------------------------------------------------------------ encoding

        private static int EntrySize(TKey key, TValue value)
        {
            KeyScratch.Reset();
            NetworkSerialization.Write(KeyScratch, key);
            ValueScratch.Reset();
            NetworkSerialization.Write(ValueScratch, value);
            return NetworkMapCodec.EntryBytes(KeyScratch.Length, ValueScratch.Length, true);
        }

        private static void WriteSet(NetworkWriter writer, TKey key, TValue value)
        {
            KeyScratch.Reset();
            NetworkSerialization.Write(KeyScratch, key);
            ValueScratch.Reset();
            NetworkSerialization.Write(ValueScratch, value);
            NetworkMapCodec.WriteSet(writer, KeyScratch.ToSegment(), ValueScratch.ToSegment());
        }

        internal override void WriteDelta(NetworkWriter writer)
        {
            int countAt = NetworkMapCodec.BeginBody(writer, _cleared);
            int n = 0;
            foreach (var key in _touched)
            {
                if (_map.TryGetValue(key, out var value)) WriteSet(writer, key, value);
                else
                {
                    // After a clear, a key that is not there needs no removal: the clear already took it.
                    if (_cleared) continue;
                    KeyScratch.Reset();
                    NetworkSerialization.Write(KeyScratch, key);
                    NetworkMapCodec.WriteRemove(writer, KeyScratch.ToSegment());
                }
                n++;
            }
            NetworkMapCodec.EndBody(writer, countAt, n);
        }

        internal override void WriteFull(NetworkWriter writer)
        {
            int countAt = NetworkMapCodec.BeginBody(writer, clear: true);
            foreach (var kv in _map) WriteSet(writer, kv.Key, kv.Value);
            NetworkMapCodec.EndBody(writer, countAt, _map.Count);
        }

        internal override void ApplyBody(NetworkReader reader)
        {
            Entries.Clear();
            NetworkMapCodec.ReadBody(reader, out bool clear, Entries);
            // Decode everything first: a body that fails to decode leaves the map as it was.
            var sets = new List<KeyValuePair<TKey, TValue>>(Entries.Count);
            var removes = new List<TKey>();
            var sizes = new List<int>(Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                EntryReader.Set(e.Key);
                var key = NetworkSerialization.Read<TKey>(EntryReader);
                if (!e.IsSet) { removes.Add(key); continue; }
                EntryReader.Set(e.Value);
                sets.Add(new KeyValuePair<TKey, TValue>(key, NetworkSerialization.Read<TValue>(EntryReader)));
                sizes.Add(NetworkMapCodec.EntryBytes(e.Key.Count, e.Value.Count, true));
            }
            Entries.Clear();

            List<NetworkMapChange<TKey, TValue>> changes = OnChanged != null ? new List<NetworkMapChange<TKey, TValue>>() : null;
            Dictionary<TKey, TValue> before = null;
            if (clear)
            {
                before = new Dictionary<TKey, TValue>(_map, _map.Comparer);
                _map.Clear();
                _sizes.Clear();
                _encodedBytes = NetworkMapCodec.HeaderBytes;
            }
            for (int i = 0; i < removes.Count; i++)
            {
                var key = removes[i];
                if (!_map.TryGetValue(key, out var old)) continue;
                _map.Remove(key);
                _encodedBytes -= _sizes[key];
                _sizes.Remove(key);
                changes?.Add(new NetworkMapChange<TKey, TValue>(NetworkMapChangeKind.Removed, key, old, default));
            }
            for (int i = 0; i < sets.Count; i++)
            {
                var kv = sets[i];
                bool had;
                TValue old;
                if (before != null) had = before.TryGetValue(kv.Key, out old);
                else had = _map.TryGetValue(kv.Key, out old);
                if (_sizes.TryGetValue(kv.Key, out int oldSize)) _encodedBytes -= oldSize;
                _map[kv.Key] = kv.Value;
                _sizes[kv.Key] = sizes[i];
                _encodedBytes += sizes[i];
                if (had && ValueComparer.Equals(old, kv.Value)) continue;
                changes?.Add(new NetworkMapChange<TKey, TValue>(had ? NetworkMapChangeKind.Updated : NetworkMapChangeKind.Added, kv.Key, old, kv.Value));
            }
            if (before != null && changes != null)
            {
                // What the full copy no longer has, reported first.
                var gone = new List<NetworkMapChange<TKey, TValue>>();
                foreach (var kv in before)
                    if (!_map.ContainsKey(kv.Key)) gone.Add(new NetworkMapChange<TKey, TValue>(NetworkMapChangeKind.Removed, kv.Key, kv.Value, default));
                changes.InsertRange(0, gone);
            }
            if (changes == null) return;
            for (int i = 0; i < changes.Count; i++)
            {
                try { OnChanged?.Invoke(changes[i]); }
                catch (Exception ex) { NebulaLog.Error($"NetworkMap {Name} OnChanged threw: {ex.Message}"); }
            }
        }

        public override string ToString() => $"NetworkMap({_map.Count})";
    }
}
