using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula.World
{
    /// <summary>
    /// The saved state of one procedurally placed object: a rock that has been mined, a plant that has been
    /// harvested. An object with no entry is <i>untouched</i>, and the game draws it from its seed as it always
    /// would. Only objects that differ from that default have an entry (see <see cref="ChunkState"/>).
    /// <para>
    /// <see cref="Value"/> means whatever the game wants it to mean, such as units left or a growth stage.
    /// <see cref="ExpiresAtUnixMs"/> is an absolute UTC time in Unix milliseconds. When that time passes, the entry
    /// is removed and the object is untouched again ("depleted until T"). Nothing runs while the object's chunk is
    /// unloaded: the time is compared when the chunk's state is next read or loaded.
    /// </para>
    /// </summary>
    public readonly struct ObjectState : IEquatable<ObjectState>
    {
        /// <summary>The game's value for the object.</summary>
        public readonly uint Value;

        /// <summary>
        /// When the entry reverts to untouched, in UTC Unix milliseconds. 0 means it never expires. Use
        /// <see cref="ChunkState.NowUnixMs"/> to compute it, for example <c>ChunkState.NowUnixMs + 600_000</c>
        /// for ten minutes from now.
        /// </summary>
        public readonly long ExpiresAtUnixMs;

        private readonly byte[] _payload;

        /// <summary>
        /// An entry with a value, an optional expiry and an optional payload of at most
        /// <see cref="ChunkState.MaxPayloadBytes"/> bytes. The payload is copied, so the caller can reuse its array.
        /// </summary>
        public ObjectState(uint value, long expiresAtUnixMs = 0, byte[] payload = null)
        {
            Value = value;
            ExpiresAtUnixMs = expiresAtUnixMs < 0 ? 0 : expiresAtUnixMs;
            if (payload == null || payload.Length == 0) _payload = null;
            else
            {
                _payload = new byte[payload.Length];
                Buffer.BlockCopy(payload, 0, _payload, 0, payload.Length);
            }
        }

        /// <summary>Wraps a payload array without copying it. For decoding only.</summary>
        internal ObjectState(uint value, long expiresAtUnixMs, byte[] payload, bool noCopy)
        {
            Value = value;
            ExpiresAtUnixMs = expiresAtUnixMs < 0 ? 0 : expiresAtUnixMs;
            _payload = payload != null && payload.Length > 0 ? payload : null;
        }

        /// <summary>
        /// Extra bytes the game stores with the entry, or an empty array. Treat it as read-only: changing it does
        /// not change the stored entry, and it can be shared with the copy that replicated it.
        /// </summary>
        public byte[] Payload => _payload ?? Array.Empty<byte>();

        /// <summary>Length of <see cref="Payload"/> in bytes.</summary>
        public int PayloadLength => _payload != null ? _payload.Length : 0;

        /// <summary>Whether the entry has an expiry time.</summary>
        public bool Expires => ExpiresAtUnixMs > 0;

        /// <summary>Whether the entry still applies at <paramref name="nowUnixMs"/>: it never expires, or its expiry time has not passed yet.</summary>
        public bool IsLiveAt(long nowUnixMs) => ExpiresAtUnixMs <= 0 || nowUnixMs < ExpiresAtUnixMs;

        /// <summary>Compares the value, the expiry time and the payload bytes.</summary>
        public bool Equals(ObjectState other)
        {
            if (Value != other.Value || ExpiresAtUnixMs != other.ExpiresAtUnixMs) return false;
            int n = PayloadLength;
            if (n != other.PayloadLength) return false;
            for (int i = 0; i < n; i++) if (_payload[i] != other._payload[i]) return false;
            return true;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is ObjectState other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)Value * 397 ^ ExpiresAtUnixMs.GetHashCode();
                int n = PayloadLength;
                for (int i = 0; i < n; i++) h = h * 31 + _payload[i];
                return h;
            }
        }

        /// <summary>Two entries are equal when their value, expiry time and payload bytes are equal.</summary>
        public static bool operator ==(ObjectState a, ObjectState b) => a.Equals(b);

        /// <summary>Two entries differ when their value, expiry time or payload bytes differ.</summary>
        public static bool operator !=(ObjectState a, ObjectState b) => !a.Equals(b);

        /// <inheritdoc/>
        public override string ToString() =>
            ExpiresAtUnixMs > 0 ? $"{Value} until {ExpiresAtUnixMs}" + (PayloadLength > 0 ? $" (+{PayloadLength} B)" : "")
                                : Value + (PayloadLength > 0 ? $" (+{PayloadLength} B)" : "");
    }

    /// <summary>What happened to an object's entry in <see cref="ObjectStateChange"/>.</summary>
    public enum ObjectStateChangeKind : byte
    {
        /// <summary>The object has an entry, new or changed. This includes the entries of a chunk whose state has just arrived in this process.</summary>
        Set = 0,
        /// <summary>The entry was removed by a write. The object is untouched again.</summary>
        Cleared = 1,
        /// <summary>The entry's <see cref="ObjectState.ExpiresAtUnixMs"/> passed. The object is untouched again.</summary>
        Expired = 2,
        /// <summary>
        /// This process no longer holds the chunk's state: the chunk left this client's interest or was unloaded
        /// here. The entry was not changed. A client that still draws the chunk can keep the object as it was.
        /// </summary>
        Forgotten = 3,
    }

    /// <summary>One change to one object's entry, raised through <see cref="ChunkState.Changed"/>.</summary>
    public readonly struct ObjectStateChange
    {
        /// <summary>The chunk (container) the object belongs to. Can be null if the container is not registered in this process.</summary>
        public readonly Container Container;
        /// <summary>The id of the chunk's container, such as <c>rt_1234</c>.</summary>
        public readonly string ContainerId;
        /// <summary>The game's id for the object.</summary>
        public readonly ulong ObjectId;
        /// <summary>What happened.</summary>
        public readonly ObjectStateChangeKind Kind;
        /// <summary>The object has an entry now. False means it is untouched, or unknown for <see cref="ObjectStateChangeKind.Forgotten"/>.</summary>
        public readonly bool HasState;
        /// <summary>The entry, when <see cref="HasState"/> is true.</summary>
        public readonly ObjectState State;
        /// <summary>
        /// Raised by the worker that holds the chunk's lease. False on clients and on workers that hold a
        /// read-only copy. A process that runs several workers (tests, the Editor) raises one change per copy.
        /// </summary>
        public readonly bool IsAuthority;

        internal ObjectStateChange(Container container, string containerId, ulong objectId, ObjectStateChangeKind kind, bool hasState, ObjectState state, bool isAuthority)
        {
            Container = container;
            ContainerId = containerId ?? "";
            ObjectId = objectId;
            Kind = kind;
            HasState = hasState;
            State = state;
            IsAuthority = isAuthority;
        }
    }

    /// <summary>Handler for <see cref="ChunkState.Changed"/>.</summary>
    public delegate void ObjectStateChangedHandler(in ObjectStateChange change);

    /// <summary>
    /// Sparse, persistent state for objects that a game places procedurally inside chunks (runtime containers). A
    /// game generates its resource nodes, plants and props from the chunk's seed on every role. Only the objects
    /// that a player changed have an entry, keyed by an object id the game chooses. The id must be stable: the same
    /// object gets the same id in every process and every run, for example its index in the chunk's generation
    /// order. The entries are grouped per chunk, replicated to clients near the chunk, saved with the chunk, and
    /// brought back when the chunk is leased again, including after a scope is retired and restored.
    /// <para>
    /// This class is the read side, and it works the same on clients and workers. A worker changes entries through
    /// <see cref="NebulaWorker.ChunkStates"/> (<see cref="ChunkStateService"/>), which routes each change to the
    /// worker that holds the chunk's lease.
    /// </para>
    /// <para>
    /// An untouched chunk costs nothing: it has no record in the store and nothing is spawned for it. The first
    /// entry in a chunk spawns one small server-driven entity in the chunk's container, and that entity carries
    /// the entries. When the chunk's last entry is cleared or expires, the entity is despawned and its record is
    /// deleted. Reads compare expiry times with <see cref="NowUnixMs"/>, so an expired entry reads as untouched even
    /// before it is removed. See the procedural state guide for the full design.
    /// </para>
    /// </summary>
    public static class ChunkState
    {
        /// <summary>Most bytes an entry's <see cref="ObjectState.Payload"/> can hold.</summary>
        public const int MaxPayloadBytes = 64;

        /// <summary>
        /// Most bytes one chunk's entries can take when encoded. An entry takes 13 bytes, plus 8 when it expires and
        /// 1 plus its length when it has a payload, so a chunk holds about 3,700 plain entries. A change that would
        /// go over the limit is refused with <see cref="ChunkStateOutcome.Rejected"/>. The limit keeps the chunk's
        /// state under the 64 KB that one replicated or persisted blob can carry.
        /// </summary>
        public const int MaxEncodedBytes = 48 * 1024;

        /// <summary>Prefix of the <see cref="PersistentEntity.Key"/> a chunk's state is saved under: <c>chunkstate:&lt;containerId&gt;</c>.</summary>
        public const string KeyPrefix = "chunkstate:";

        /// <summary>The store key a chunk's state is saved under.</summary>
        public static string KeyOf(string containerId) => KeyPrefix + (containerId ?? "");

        private static readonly Dictionary<string, List<ChunkStateEntity>> Copies = new Dictionary<string, List<ChunkStateEntity>>(StringComparer.Ordinal);
        private static readonly List<ChunkStateEntity> AllCopies = new List<ChunkStateEntity>();
        private static readonly List<ChunkStateEntity> PollScratch = new List<ChunkStateEntity>();
        private static InterestSettings _interest = InterestSettings.Default;
        private static bool _warnedClamp;

        /// <summary>
        /// Test seam: replaces <see cref="NowUnixMs"/>. Null uses the default clock.
        /// </summary>
        internal static Func<long> Clock;

        /// <summary>
        /// The current time in UTC Unix milliseconds, as this process compares expiry times. A worker uses its
        /// system clock (workers already share a clock for the simulation tick). A client uses its own clock,
        /// corrected by how far the server's tick is from the tick the client's clock would give, so a client with
        /// a wrong clock still sees entries expire when the server does, to within its latency.
        /// </summary>
        public static long NowUnixMs => Clock != null ? Clock() : DefaultNow();

        private static readonly long TickOriginUnixMs = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        private static long DefaultNow()
        {
            long local = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (NebulaRuntime.IsServer || !NebulaRuntime.IsClient) return local;
            uint server = NetworkTime.LatestServerTick;
            if (server == 0) return local;
            // Both ticks count from the same origin at the same rate, and the difference of two uint ticks is safe
            // across the wrap. A difference of more than a day is not clock skew, so it is ignored.
            int ticks = unchecked((int)(server - NetworkTime.DerivedTick));
            long offset = ticks * 1000L / NetworkTime.TickRate;
            return Math.Abs(offset) < 86_400_000L ? local + offset : local;
        }

        /// <summary>
        /// Raised on every role when an object's entry changes in this process: set or cleared by a write (on the
        /// worker that made it, and on every copy when the change replicates), expired, arrived with a chunk's
        /// state, or forgotten. Use it to update the object's visuals or colliders.
        /// </summary>
        public static event ObjectStateChangedHandler Changed;

        /// <summary>
        /// The entry of <paramref name="objectId"/> in <paramref name="chunk"/>. False when the object is untouched,
        /// its entry has expired, or this process does not hold the chunk's state (it is not near the chunk).
        /// </summary>
        public static bool TryGet(Container chunk, ulong objectId, out ObjectState state)
        {
            state = default;
            return chunk != null && TryGet(chunk.ContainerId, objectId, out state);
        }

        /// <summary>The entry of <paramref name="objectId"/> in the chunk whose container id is <paramref name="containerId"/>. See <see cref="TryGet(Container, ulong, out ObjectState)"/>.</summary>
        public static bool TryGet(string containerId, ulong objectId, out ObjectState state)
        {
            state = default;
            var copy = Best(containerId);
            return copy != null && copy.TryGetLive(objectId, NowUnixMs, out state);
        }

        /// <summary>
        /// Whether this process holds the state of the chunk right now. A chunk with no entries has no state to
        /// hold, so false means "untouched, or not replicated here".
        /// </summary>
        public static bool Holds(string containerId) => Best(containerId) != null;

        /// <summary>How many objects in the chunk have an entry that has not expired, as far as this process knows.</summary>
        public static int CountIn(string containerId)
        {
            var copy = Best(containerId);
            return copy != null ? copy.CountLive(NowUnixMs) : 0;
        }

        /// <summary>
        /// Add every entry of the chunk that has not expired to <paramref name="into"/> (cleared first), in object
        /// id order. Use it to apply a chunk's state in one pass, for example after the chunk is built.
        /// </summary>
        public static void GetAll(string containerId, List<KeyValuePair<ulong, ObjectState>> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            into.Clear();
            var copy = Best(containerId);
            if (copy != null) copy.CollectLive(NowUnixMs, into);
        }

        /// <summary>
        /// The clients a chunk's state reaches. The entity is at the chunk's centre, so its relevance radius is the
        /// interest radius plus the chunk's half diagonal (horizontal only when interest is planar): every client
        /// within the interest radius of any point of the chunk receives the chunk's state. The result is clamped
        /// to <see cref="InterestSettings.MaxRadius"/>.
        /// </summary>
        public static float RelevanceRadiusFor(Container chunk)
        {
            var settings = _interest;
            float radius = settings.Radius > 0f ? settings.Radius : InterestSettings.Default.Radius;
            if (chunk == null) return radius;
            var half = chunk.Size * 0.5f;
            float extent = settings.Planar ? new Vector2(half.x, half.z).magnitude : half.magnitude;
            float wanted = radius + extent;
            float max = settings.MaxRadius > 0f ? settings.MaxRadius : wanted;
            if (wanted > max)
            {
                if (!_warnedClamp)
                {
                    _warnedClamp = true;
                    NebulaLog.Warn($"chunk state of {chunk.ContainerId} should reach {wanted:0} m (InterestRadius {radius:0} m plus half the chunk's diagonal), but InterestMaxRadius is {max:0} m. Clients near a chunk's edge may not receive its state; raise InterestMaxRadius or use smaller chunks.");
                }
                return max;
            }
            return wanted;
        }

        // ------------------------------------------------------------------------------------------ internals

        /// <summary>The interest settings the relevance radius is derived from. Set by the worker's service.</summary>
        internal static void UseInterest(InterestSettings settings) => _interest = settings;

        /// <summary>The copy to read: the authoritative one when this process has one, otherwise any.</summary>
        internal static ChunkStateEntity Best(string containerId)
        {
            if (string.IsNullOrEmpty(containerId) || !Copies.TryGetValue(containerId, out var list)) return null;
            ChunkStateEntity any = null;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null) continue;
                if (c.HasAuthority) return c;
                if (any == null) any = c;
            }
            return any;
        }

        /// <summary>Every live copy of a chunk's state in this process (several only when one process runs several workers).</summary>
        internal static IReadOnlyList<ChunkStateEntity> CopiesOf(string containerId) =>
            !string.IsNullOrEmpty(containerId) && Copies.TryGetValue(containerId, out var list) ? list : (IReadOnlyList<ChunkStateEntity>)Array.Empty<ChunkStateEntity>();

        /// <summary>Every live copy in this process.</summary>
        internal static IReadOnlyList<ChunkStateEntity> All => AllCopies;

        internal static void Register(ChunkStateEntity copy)
        {
            if (copy == null || string.IsNullOrEmpty(copy.ContainerId)) return;
            if (!Copies.TryGetValue(copy.ContainerId, out var list))
            {
                list = new List<ChunkStateEntity>(1);
                Copies[copy.ContainerId] = list;
            }
            if (!list.Contains(copy)) list.Add(copy);
            if (!AllCopies.Contains(copy)) AllCopies.Add(copy);
        }

        internal static void Unregister(ChunkStateEntity copy, string containerId)
        {
            if (copy == null) return;
            AllCopies.Remove(copy);
            if (string.IsNullOrEmpty(containerId) || !Copies.TryGetValue(containerId, out var list)) return;
            list.Remove(copy);
            if (list.Count == 0) Copies.Remove(containerId);
        }

        internal static void Raise(in ObjectStateChange change)
        {
            var handler = Changed;
            if (handler == null) return;
            try { handler(in change); }
            catch (Exception e) { NebulaLog.Error($"ChunkState.Changed handler threw for {change.ContainerId}/{change.ObjectId}: {e}"); }
        }

        /// <summary>Whether anything listens to <see cref="Changed"/>, so a copy can skip building changes nobody reads.</summary>
        internal static bool HasListeners => Changed != null;

        /// <summary>
        /// Remove the entries whose time has passed from every copy in this process and raise
        /// <see cref="ObjectStateChangeKind.Expired"/> for each one. Each copy also does this from its own
        /// <c>Update</c> in play mode; this is for tests and edit-mode tools.
        /// </summary>
        internal static void PollExpiries()
        {
            long now = NowUnixMs;
            PollScratch.Clear();
            PollScratch.AddRange(AllCopies);
            for (int i = 0; i < PollScratch.Count; i++)
                if (PollScratch[i] != null) PollScratch[i].PollExpiry(now);
            PollScratch.Clear();
        }

        /// <summary>New play session without a domain reload: forget every copy and every subscriber.</summary>
        internal static void ResetForNewSession()
        {
            Copies.Clear();
            AllCopies.Clear();
            Changed = null;
            Clock = null;
            _interest = InterestSettings.Default;
            _warnedClamp = false;
        }
    }
}
