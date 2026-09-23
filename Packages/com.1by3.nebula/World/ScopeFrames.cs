using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula.World
{
    /// <summary>
    /// One scope's floating origin: which cell of <i>that scope's</i> grid currently sits at Unity's (0,0,0), and
    /// the delta every frame position of that scope gets when it moves. The public world's frame is
    /// <see cref="ScopeFrames.Public"/>, which is <see cref="WorldOrigin"/> itself, so a project with no scopes
    /// behaves exactly as it always did.
    /// <para>
    /// A worker may hold containers of many scopes whose coordinates have nothing to do with each other. Each of
    /// them keeps its own origin cell, so two scopes can both sit near Unity's origin on one worker without
    /// interfering: they are already in separate physics scenes (<c>InstanceScenes</c>) and separate
    /// container graphs. Design record: <c>docs/scope-frames.md</c>.
    /// </para>
    /// </summary>
    public sealed class ScopeFrame
    {
        /// <summary>The opaque scope key (<c>""</c> is the public world).</summary>
        public string ScopeKey { get; }

        /// <summary>The 64-bit isolation id of <see cref="ScopeKey"/>; 0 for the public world.</summary>
        public ulong InstanceId { get; }

        /// <summary>Whether this is the public world's frame, which is <see cref="WorldOrigin"/>.</summary>
        public bool IsPublic => InstanceId == 0;

        private Vector3Int _cell;
        private Vector3 _cellSize;
        private int _shiftCount;

        internal ScopeFrame(string scopeKey, ulong instanceId, Vector3 cellSize)
        {
            ScopeKey = scopeKey ?? "";
            InstanceId = instanceId;
            _cellSize = cellSize;
        }

        /// <summary>The cell of this scope's grid that sits at Unity's origin.</summary>
        public Vector3Int Cell => IsPublic ? WorldOrigin.Cell : _cell;

        /// <summary>Size of one cell of this scope's grid, in metres. The public frame's is the world definition's.</summary>
        public Vector3 CellSize
        {
            get
            {
                if (!IsPublic) return _cellSize;
                var world = WorldOrigin.Definition;
                return world != null ? world.CellSize : Vector3.zero;
            }
            internal set { if (!IsPublic) _cellSize = value; }
        }

        /// <summary>How many times this scope's origin has moved.</summary>
        public int ShiftCount => IsPublic ? WorldOrigin.ShiftCount : _shiftCount;

        /// <summary>
        /// Raised after this scope's containers were moved: the delta to add to any other frame position of this
        /// scope (cached positions, history buffers, world-space effects). The public frame re-raises
        /// <see cref="WorldOrigin.Shifted"/>.
        /// </summary>
        public event Action<Vector3> Shifted;

        /// <summary>Where absolute (0,0,0) sits in this scope's frame: what turns an absolute box into a frame box.</summary>
        public Vector3 OriginOffset => new Vector3(
            (float)(-(long)Cell.x * (double)CellSize.x),
            (float)(-(long)Cell.y * (double)CellSize.y),
            (float)(-(long)Cell.z * (double)CellSize.z));

        /// <summary>
        /// <see cref="OriginOffset"/> in double, per axis: what an absolute position kept in double is shifted by before
        /// it is narrowed to float, so a placement far from the origin keeps its precision.
        /// </summary>
        public void OriginOffsetPrecise(out double x, out double y, out double z)
        {
            x = -(long)Cell.x * (double)CellSize.x;
            y = -(long)Cell.y * (double)CellSize.y;
            z = -(long)Cell.z * (double)CellSize.z;
        }

        /// <summary>Delta every frame position of this scope gets when its origin moves from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public Vector3 ShiftDelta(Vector3Int from, Vector3Int to) => ShiftDelta(CellSize, from, to);

        /// <summary>Pure form of <see cref="ShiftDelta(Vector3Int,Vector3Int)"/>; the same arithmetic <see cref="WorldOrigin.ShiftDelta"/> does.</summary>
        public static Vector3 ShiftDelta(Vector3 cellSize, Vector3Int from, Vector3Int to) => new Vector3(
            (float)(((long)from.x - to.x) * (double)cellSize.x),
            (float)(((long)from.y - to.y) * (double)cellSize.y),
            (float)(((long)from.z - to.z) * (double)cellSize.z));

        /// <summary>
        /// Record the move and tell this scope's listeners. The caller has already moved the scope's containers and
        /// entities; the public frame is moved through <see cref="WorldOrigin"/> instead and only re-raises here.
        /// </summary>
        internal void Apply(Vector3Int cell, Vector3 delta)
        {
            if (!IsPublic)
            {
                _cell = cell;
                _shiftCount++;
            }
            Shifted?.Invoke(delta);
            ScopeFrames.RaiseShifted(this, delta);
        }

        internal void Raise(Vector3 delta) => Shifted?.Invoke(delta);

        public override string ToString() => $"frame({(IsPublic ? "public" : ScopeKey)} @ {Cell}, cell {CellSize})";
    }

    /// <summary>
    /// The origin frames this process holds, one per scope. The public world's frame always exists and is
    /// <see cref="WorldOrigin"/>; a scope gets one the first time this process names it, which on a worker is the
    /// first time one of its containers turns up here (<c>docs/scoped-chunk-grids.md</c> D7).
    /// <para>
    /// A container or an entity whose scope has <b>no</b> frame here is in the public frame: that is what keeps an
    /// instance scope (a room, a dungeon) behaving exactly as it did before per-scope frames existed. Only a scope
    /// that asked for a frame of its own — a scoped chunk grid — leaves it.
    /// </para>
    /// </summary>
    public static class ScopeFrames
    {
        private static readonly Dictionary<ulong, ScopeFrame> ByInstance = new Dictionary<ulong, ScopeFrame>();
        private static readonly Dictionary<string, ScopeFrame> ByScope = new Dictionary<string, ScopeFrame>(StringComparer.Ordinal);

        /// <summary>The public world's frame. Its cell, shift count and event are <see cref="WorldOrigin"/>'s.</summary>
        public static ScopeFrame Public { get; } = new ScopeFrame("", 0UL, Vector3.zero);

        /// <summary>Every frame this process holds, the public one first.</summary>
        public static IEnumerable<ScopeFrame> All
        {
            get
            {
                yield return Public;
                foreach (var frame in ByInstance.Values) yield return frame;
            }
        }

        /// <summary>Scoped frames only (the public one is always there and is not one of these).</summary>
        public static int ScopedCount => ByInstance.Count;

        /// <summary>Raised after any frame moved: the frame and its delta. The public frame reports here too.</summary>
        public static event Action<ScopeFrame, Vector3> AnyShifted;

        internal static void RaiseShifted(ScopeFrame frame, Vector3 delta) => AnyShifted?.Invoke(frame, delta);

        static ScopeFrames()
        {
            WorldOrigin.Shifted += delta => { Public.Raise(delta); AnyShifted?.Invoke(Public, delta); };
        }

        /// <summary>Whether <paramref name="instanceId"/> has a frame of its own here (false for the public world and for any scope that shares it).</summary>
        public static bool HasFrame(ulong instanceId) => instanceId != 0 && ByInstance.ContainsKey(instanceId);

        /// <summary>
        /// The frame <paramref name="instanceId"/>'s containers live in: its own when it has one, the public frame
        /// otherwise. Never null, so every caller has one answer to convert with.
        /// </summary>
        public static ScopeFrame Of(ulong instanceId) =>
            instanceId != 0 && ByInstance.TryGetValue(instanceId, out var frame) ? frame : Public;

        /// <summary>The frame of a scope key; the public frame for <c>""</c> and for a scope with none of its own.</summary>
        public static ScopeFrame Of(string scopeKey) =>
            !string.IsNullOrEmpty(scopeKey) && ByScope.TryGetValue(scopeKey, out var frame) ? frame : Public;

        /// <summary>
        /// The id of the frame a scope's containers move with: the scope's own id when it has a frame, 0 (the public
        /// frame) otherwise. This is the key an origin shift is addressed to.
        /// </summary>
        public static ulong FrameIdOf(ulong instanceId) => HasFrame(instanceId) ? instanceId : 0UL;

        /// <summary>
        /// Give a scope a frame of its own, or return the one it has. The public world's is <see cref="Public"/> and
        /// is never created here. The cell size is the scope's grid's; a later call with a different one updates it
        /// (a definition is validated once at activation, so this only ever refines a frame made from an inference).
        /// </summary>
        public static ScopeFrame Ensure(string scopeKey, ulong instanceId, Vector3 cellSize)
        {
            if (instanceId == 0 || string.IsNullOrEmpty(scopeKey)) return Public;
            if (ByInstance.TryGetValue(instanceId, out var frame))
            {
                if (cellSize.x > 0f && cellSize.y > 0f && cellSize.z > 0f) frame.CellSize = cellSize;
                return frame;
            }
            frame = new ScopeFrame(scopeKey, instanceId, cellSize);
            ByInstance[instanceId] = frame;
            ByScope[scopeKey] = frame;
            return frame;
        }

        /// <summary>Forget a scope's frame (its last container went away). The public frame cannot be removed.</summary>
        public static void Remove(string scopeKey)
        {
            if (string.IsNullOrEmpty(scopeKey) || !ByScope.TryGetValue(scopeKey, out var frame)) return;
            ByScope.Remove(scopeKey);
            ByInstance.Remove(frame.InstanceId);
        }

        /// <summary>New play session, or the world was unloaded: every scoped frame goes, the public one is reset by <see cref="WorldOrigin.Reset"/>.</summary>
        internal static void Clear()
        {
            ByInstance.Clear();
            ByScope.Clear();
        }
    }
}
