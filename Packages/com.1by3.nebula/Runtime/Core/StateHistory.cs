using System;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Marks a <see cref="NetworkVariable{T}"/> field as part of the entity's recorded state history, so its value
    /// is snapshotted every tick alongside the pose and can be read back through
    /// <see cref="NetworkIdentity.StateAt"/> / <see cref="HistoricalState.TryGetValue{T}"/>. Pose, velocity and
    /// container are always recorded; a variable is not, because a snapshot costs a serialization of its value
    /// every tick on every worker that holds a copy.
    /// <code>
    /// public sealed class Pawn : NetworkBehaviour
    /// {
    ///     [SyncHistory] public NetworkVariable&lt;byte&gt; Stance = new NetworkVariable&lt;byte&gt;();
    /// }
    /// </code>
    /// Recording happens only on a worker (<see cref="NebulaRuntime.IsServer"/>) and only while
    /// <see cref="NebulaConfig.StateHistoryTicks"/> is above zero. See <c>docs/state-history.md</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = true, AllowMultiple = false)]
    public sealed class SyncHistoryAttribute : Attribute
    {
    }

    /// <summary>
    /// What an entity looked like at one recorded server tick: the answer of
    /// <see cref="NetworkIdentity.StateAt"/>. Check <see cref="Available"/> first; every other member is
    /// meaningless when it is false. <see cref="Tick"/> is the tick the entry was actually recorded for, which is
    /// the asked-for tick unless it fell in a gap (see <see cref="StateHistory.GapToleranceTicks"/>).
    /// </summary>
    public readonly struct HistoricalState
    {
        private readonly StateHistory _history;
        private readonly int _slot;

        /// <summary>True when an entry was found; false means "unavailable", never an extrapolation.</summary>
        public bool Available { get; }
        /// <summary>The server tick this entry belongs to.</summary>
        public uint Tick { get; }
        /// <summary>World position at <see cref="Tick"/>.</summary>
        public Vector3 Position { get; }
        /// <summary>World rotation at <see cref="Tick"/>.</summary>
        public Quaternion Rotation { get; }
        /// <summary>World-space linear velocity as the authority reported it at <see cref="Tick"/>.</summary>
        public Vector3 Velocity { get; }
        /// <summary>The container the entity was in at <see cref="Tick"/> (null: outside every container).</summary>
        public Container Container { get; }
        /// <summary>The entity's authority epoch at <see cref="Tick"/>.</summary>
        public uint Epoch { get; }
        /// <summary>
        /// True when this worker recorded the entry as the authority (after that tick's simulation), false when it
        /// recorded it as a ghost holder from the owner's stream. A ghost entry is one tick behind the owner in
        /// availability, never in the tick it is tagged with; see <c>docs/state-history.md</c> §4.
        /// </summary>
        public bool FromAuthority { get; }

        internal HistoricalState(StateHistory history, int slot, uint tick, Vector3 position, Quaternion rotation,
            Vector3 velocity, Container container, uint epoch, bool fromAuthority)
        {
            _history = history;
            _slot = slot;
            Available = true;
            Tick = tick;
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
            Container = container;
            Epoch = epoch;
            FromAuthority = fromAuthority;
        }

        /// <summary>
        /// The value <paramref name="variable"/> held at <see cref="Tick"/>. False when the variable does not carry
        /// <see cref="SyncHistoryAttribute"/>, belongs to another entity, or nothing was snapshotted for this entry.
        /// </summary>
        public bool TryGetValue<T>(NetworkVariable<T> variable, out T value)
        {
            if (Available && _history != null && variable != null) return _history.TryReadValue(_slot, Tick, variable, out value);
            value = default;
            return false;
        }

        public override string ToString() =>
            Available ? $"t{Tick} {Position} {(FromAuthority ? "authority" : "ghost")}" : "unavailable";
    }

    /// <summary>
    /// The bounded per-entity ring of recorded ticks behind <see cref="NetworkIdentity.StateAt"/>: pose, velocity,
    /// container, epoch and the <see cref="SyncHistoryAttribute"/> variables, one entry per server tick, indexed by
    /// <c>tick % capacity</c>. One instance per <see cref="NetworkIdentity"/>, created on the first recorded tick.
    /// <para>
    /// The ring and the per-slot buffers are allocated once and reused: recording a tick copies into them and never
    /// allocates after the buffers have reached the size the entity's marked variables need.
    /// </para>
    /// Design record: <c>docs/state-history.md</c>.
    /// </summary>
    public sealed class StateHistory
    {
        /// <summary>Ticks kept per entity when the game does not say otherwise (<see cref="NebulaConfig.StateHistoryTicks"/>).</summary>
        public const int DefaultWindowTicks = 32;

        /// <summary>The most ticks a window may be asked for. A larger value is clamped to this.</summary>
        public const int MaxWindowTicks = 1024;

        /// <summary>
        /// How far <see cref="TryGetStateAt"/> looks either side of the asked-for tick when that tick itself was not
        /// recorded (a ghost whose owner sent nothing that tick because nothing moved). The entry returned says which
        /// tick it is; nothing is interpolated or extrapolated.
        /// </summary>
        public const int GapToleranceTicks = 4;

        /// <summary>
        /// Ticks of history every entity in this process keeps, from <see cref="NebulaConfig.StateHistoryTicks"/>.
        /// Zero turns recording off entirely. Set by <see cref="NebulaWorker"/> at start-up; a client leaves it at
        /// zero, because history is a worker-side facility (the client has <see cref="RemoteInterpolator"/>).
        /// </summary>
        public static int WindowTicks { get; internal set; }

        /// <summary>Restore the process-wide window to "off". Called from <see cref="NebulaRuntime.Reset"/>.</summary>
        internal static void ResetWindow() => WindowTicks = 0;

        private struct Entry
        {
            public uint Tick;
            public bool Valid;
            public bool FromAuthority;
            public uint Epoch;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Velocity;
            public Container Container;
            /// <summary>Serialized values of the marked variables, in <see cref="_vars"/> order.</summary>
            public byte[] Fields;
            /// <summary>Start of each marked variable inside <see cref="Fields"/>, plus the total length at the end.</summary>
            public int[] Offsets;
        }

        private static readonly NetworkWriter FieldWriter = new NetworkWriter(256);
        private static readonly NetworkReader FieldReader = new NetworkReader();

        private readonly Entry[] _ring;
        private NetworkVariableBase[] _vars = Array.Empty<NetworkVariableBase>();
        private uint _newest;
        private bool _any;

        /// <summary>Ticks this ring can hold.</summary>
        public int Capacity => _ring.Length;

        /// <summary>True once at least one tick has been recorded.</summary>
        public bool HasEntries => _any;

        /// <summary>The newest recorded tick; meaningless when <see cref="HasEntries"/> is false.</summary>
        public uint NewestAvailableTick => _newest;

        /// <summary>
        /// The oldest tick still in the ring: the smallest recorded tick inside the window that ends at
        /// <see cref="NewestAvailableTick"/>. A tick nothing was recorded for (nothing moved, so nothing was
        /// streamed) does not shorten it; only the ring's capacity does. Meaningless when
        /// <see cref="HasEntries"/> is false.
        /// </summary>
        public uint OldestAvailableTick
        {
            get
            {
                if (!_any) return 0;
                uint floor = _newest >= (uint)_ring.Length ? _newest - (uint)_ring.Length + 1 : 0;
                uint oldest = _newest;
                for (int i = 0; i < _ring.Length; i++)
                {
                    ref var e = ref _ring[i];
                    if (e.Valid && e.Tick >= floor && e.Tick < oldest) oldest = e.Tick;
                }
                return oldest;
            }
        }

        internal StateHistory(int capacity, NetworkVariableBase[] vars)
        {
            _ring = new Entry[Math.Max(1, Math.Min(capacity, MaxWindowTicks))];
            _vars = vars ?? Array.Empty<NetworkVariableBase>();
        }

        /// <summary>The marked variables were re-discovered (a re-initialised identity): point at the new array.</summary>
        internal void Rebind(NetworkVariableBase[] vars) => _vars = vars ?? Array.Empty<NetworkVariableBase>();

        /// <summary>
        /// Record one tick. <paramref name="tick"/> is the server tick the pose belongs to: the worker's own tick on
        /// the authority, the owner's tick carried by the stream on a ghost holder. An older tick than the newest
        /// already recorded is ignored, so a late or replayed packet cannot rewrite history.
        /// </summary>
        internal void Record(uint tick, Vector3 position, Quaternion rotation, Vector3 velocity, Container container,
            uint epoch, bool fromAuthority)
        {
            if (_any && tick <= _newest) return;
            ref var e = ref _ring[tick % _ring.Length];
            e.Tick = tick;
            e.Valid = true;
            e.FromAuthority = fromAuthority;
            e.Epoch = epoch;
            e.Position = position;
            e.Rotation = rotation;
            e.Velocity = velocity;
            e.Container = container;
            RecordFields(ref e);
            _newest = tick;
            _any = true;
        }

        private void RecordFields(ref Entry e)
        {
            int count = _vars.Length;
            if (count == 0)
            {
                if (e.Offsets != null) e.Offsets[0] = 0;
                return;
            }
            FieldWriter.Reset();
            if (e.Offsets == null || e.Offsets.Length != count + 1) e.Offsets = new int[count + 1];
            for (int i = 0; i < count; i++)
            {
                e.Offsets[i] = FieldWriter.Length;
                _vars[i].Write(FieldWriter);
            }
            e.Offsets[count] = FieldWriter.Length;
            int length = FieldWriter.Length;
            if (e.Fields == null || e.Fields.Length < length) e.Fields = new byte[Math.Max(length, 32)];
            Array.Copy(FieldWriter.Buffer, 0, e.Fields, 0, length);
        }

        /// <summary>
        /// The entry for <paramref name="tick"/>, or the nearest recorded one within
        /// <see cref="GapToleranceTicks"/>, preferring the later side. False when the tick is outside the window or
        /// no entry is near it: never an extrapolation.
        /// </summary>
        internal bool TryGetStateAt(uint tick, out HistoricalState state)
        {
            state = default;
            if (!_any) return false;
            // Outside the window the answer is "unavailable", never the nearest edge: a hit test that silently
            // used the oldest entry for a tick a second older would report hits against a pose nobody was at.
            uint oldest = OldestAvailableTick;
            if (tick < oldest || tick > _newest) return false;
            if (TryRead(tick, out state)) return true;
            for (uint step = 1; step <= GapToleranceTicks; step++)
            {
                if (tick + step <= _newest && TryRead(tick + step, out state)) return true;
                if (tick >= step && tick - step >= oldest && TryRead(tick - step, out state)) return true;
            }
            return false;
        }

        private bool TryRead(uint tick, out HistoricalState state)
        {
            int slot = (int)(tick % (uint)_ring.Length);
            ref var e = ref _ring[slot];
            if (!e.Valid || e.Tick != tick)
            {
                state = default;
                return false;
            }
            state = new HistoricalState(this, slot, e.Tick, e.Position, e.Rotation, e.Velocity, e.Container, e.Epoch, e.FromAuthority);
            return true;
        }

        internal bool TryReadValue<T>(int slot, uint tick, NetworkVariable<T> variable, out T value)
        {
            value = default;
            if (slot < 0 || slot >= _ring.Length) return false;
            ref var e = ref _ring[slot];
            if (!e.Valid || e.Tick != tick || e.Fields == null || e.Offsets == null) return false;
            int index = Array.IndexOf(_vars, variable);
            if (index < 0 || index + 1 >= e.Offsets.Length) return false;
            int start = e.Offsets[index];
            int length = e.Offsets[index + 1] - start;
            if (length < 0 || start + length > e.Fields.Length) return false;
            FieldReader.Set(new ArraySegment<byte>(e.Fields, start, length));
            value = NetworkSerialization.Read<T>(FieldReader);
            return true;
        }

        /// <summary>
        /// The floating origin moved: recorded world positions are in the old frame, so move them with it. Entries
        /// under a container are left alone; their container moved with the frame and the pose is rebuilt from it.
        /// </summary>
        internal void Shift(Vector3 delta)
        {
            for (int i = 0; i < _ring.Length; i++) if (_ring[i].Valid && _ring[i].Container == null) _ring[i].Position += delta;
        }

        /// <summary>Forget everything recorded so far (the entity changed authority, or was rebound to a new copy).</summary>
        internal void Clear()
        {
            for (int i = 0; i < _ring.Length; i++) _ring[i].Valid = false;
            _any = false;
            _newest = 0;
        }
    }
}
