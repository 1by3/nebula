using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula.World
{
    /// <summary>
    /// Maps runtime containers to a planar or three-dimensional grid. Provides a stable 64-bit id per cell,
    /// cell bounds in the current floating-origin frame, and neighborhood queries.
    /// <para>
    /// Each id packs three signed 21-bit coordinates. Keep this format stable because container leases and
    /// persisted entities use these ids to identify cells.
    /// </para>
    /// </summary>
    public sealed class RuntimeGrid
    {
        /// <summary>Coordinates outside [MinCoordinate, MaxCoordinate] on any axis cannot be packed into a runtime id.</summary>
        public const int MinCoordinate = -(1 << 20);
        public const int MaxCoordinate = (1 << 20) - 1;
        private const ulong Mask = (1UL << 21) - 1;

        /// <summary>Size of one cell in meters, per axis.</summary>
        public Vector3 CellSize { get; }

        /// <summary>
        /// Cells are columns: the grid has a single layer at y = 0 and <see cref="CellSize"/>.y is the column's
        /// height, centered on absolute y = 0. Surface worlds want this — a chunk that is 64 m wide and 512 m tall
        /// is one container nothing ever leaves vertically, so verticality never costs a cell, a lease, or a
        /// neighborhood dimension. The packing is unchanged (y is simply always 0), so a planar and a volumetric
        /// grid produce the same ids for the same coordinates.
        /// </summary>
        public bool Planar { get; }

        /// <summary>
        /// The scope this grid's chunks belong to: <c>""</c> is the public world, anything else a scope activated
        /// with <see cref="ScopeKind.Grid"/>. It is what makes chunk (x,y,z) of two worlds two different
        /// containers: it goes into every id this grid hands out (<see cref="IdOf"/>).
        /// </summary>
        public string ScopeKey { get; }

        /// <summary>The 64-bit isolation id of <see cref="ScopeKey"/> (<see cref="ScopeKeys.Hash"/>); 0 for the public world.</summary>
        public ulong InstanceId { get; }

        /// <summary>Whether this is the public world's grid, whose ids are the pinned packing and nothing else.</summary>
        public bool IsPublic => InstanceId == 0;

        // A scoped grid's ids are a hash, so they cannot be unpacked. Both directions are memoised as coordinates
        // are named (by this process) or adopted from a lease row (by any other process); a chunk nobody has
        // mentioned costs nothing.
        private readonly Dictionary<Vector3Int, ulong> _idByCoord;
        private readonly Dictionary<ulong, Vector3Int> _coordById;

        public RuntimeGrid(float cellSize) : this(new Vector3(cellSize, cellSize, cellSize), false) { }

        public RuntimeGrid(Vector3 cellSize) : this(cellSize, false) { }

        public RuntimeGrid(Vector3 cellSize, bool planar) : this(cellSize, planar, "") { }

        public RuntimeGrid(Vector3 cellSize, bool planar, string scopeKey)
        {
            if (cellSize.x <= 0f || cellSize.y <= 0f || cellSize.z <= 0f)
                throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive on every axis.");
            CellSize = cellSize;
            Planar = planar;
            ScopeKey = scopeKey ?? "";
            InstanceId = ScopeKey.Length == 0 ? 0UL : ScopeKeys.Hash(ScopeKey);
            if (InstanceId == 0) return;
            _idByCoord = new Dictionary<Vector3Int, ulong>();
            _coordById = new Dictionary<ulong, Vector3Int>();
        }

        /// <summary>The grid a <see cref="ChunkGridDefinition"/> describes, for the scope it was activated under.</summary>
        public static RuntimeGrid From(ChunkGridDefinition definition, string scopeKey) =>
            definition == null ? null : new RuntimeGrid(definition.CellSize, definition.Planar, scopeKey);

        private ScopeFrame _frame;

        /// <summary>
        /// This grid's floating-origin frame: <see cref="WorldOrigin"/> for the public world, and a frame of its own
        /// for every other scope. Every piece of arithmetic below is relative to it, which is what lets two scopes on
        /// one worker both sit near Unity's origin (<c>docs/scope-frames.md</c>).
        /// </summary>
        public ScopeFrame Frame => _frame ??= IsPublic ? ScopeFrames.Public : ScopeFrames.Ensure(ScopeKey, InstanceId, CellSize);

        // ------------------------------------------------------------------------------------------ ids

        /// <summary>
        /// This grid's runtime container id for a chunk: the pinned packing in the public world, the scope's
        /// derivation (<see cref="ChunkKeys.RuntimeId"/>) in every other scope. The one place a chunk id is made;
        /// <see cref="PackId"/> stays public-world only and stays pinned.
        /// </summary>
        public ulong IdOf(Vector3Int coord)
        {
            coord = Normalize(coord);
            if (_idByCoord == null) return PackId(coord);
            if (_idByCoord.TryGetValue(coord, out ulong id)) return id;
            id = ChunkKeys.RuntimeId(ScopeKey, coord);
            _idByCoord[coord] = id;
            _coordById[id] = coord;
            return id;
        }

        /// <summary>This grid's control-plane container id for a chunk (<c>rt_</c> and <see cref="IdOf"/>).</summary>
        public string ContainerIdOf(Vector3Int coord) => ContainerRegistry.RuntimeContainerId(IdOf(coord));

        /// <summary>
        /// The chunk an id names in this grid, or false when the id is not this grid's. Always true for the public
        /// grid (every 64-bit value unpacks to a coordinate); for a scoped grid it is true for a chunk this process
        /// has named or adopted from a lease row (<see cref="Adopt"/>).
        /// </summary>
        public bool TryCoordOf(ulong id, out Vector3Int coord)
        {
            if (_coordById == null) { coord = UnpackId(id); return true; }
            return _coordById.TryGetValue(id, out coord);
        }

        /// <summary>
        /// Learn the coordinate of a chunk this process never asked for, from the part id its lease row carries
        /// (<see cref="InstanceContainerInfo.PartId"/>). Returns false when the part is not a chunk of this grid,
        /// which is how an instance's <c>interior</c> part is told apart from a chunk.
        /// </summary>
        public bool Adopt(ulong id, string partId, out Vector3Int coord)
        {
            coord = default;
            if (!ChunkKeys.TryParsePartId(partId, out var parsed)) return false;
            parsed = Normalize(parsed);
            if (_coordById == null) { coord = parsed; return PackId(parsed) == id; }
            if (ChunkKeys.RuntimeId(ScopeKey, parsed) != id) return false;
            _idByCoord[parsed] = id;
            _coordById[id] = parsed;
            coord = parsed;
            return true;
        }

        /// <summary>Whether a container is a chunk of this grid: same scope, and an id this grid can place.</summary>
        public bool Owns(Container container) =>
            container != null && container.IsRuntime && container.InstanceId == InstanceId && TryCoordOf(container.RuntimeId, out _);

        /// <summary>Whether a coordinate is within the range that <see cref="PackId"/> can represent.</summary>
        public static bool IsValid(Vector3Int c) => c.x >= MinCoordinate && c.x <= MaxCoordinate &&
            c.y >= MinCoordinate && c.y <= MaxCoordinate && c.z >= MinCoordinate && c.z <= MaxCoordinate;

        /// <summary>
        /// Pack a grid coordinate into a stable 64-bit runtime container id: three signed 21-bit fields, x in the
        /// high bits. The known values are pinned by <c>RuntimeGridTests</c> because ids are already persisted; this
        /// packing must never change.
        /// </summary>
        public static ulong PackId(Vector3Int coord)
        {
            if (!IsValid(coord)) throw new ArgumentOutOfRangeException(nameof(coord), "Grid coordinate exceeds the packed runtime id range.");
            return (((ulong)(uint)coord.x & Mask) << 42) | (((ulong)(uint)coord.y & Mask) << 21) | ((ulong)(uint)coord.z & Mask);
        }

        private static int Signed(ulong value)
        {
            int c = (int)(value & Mask);
            return c >= (1 << 20) ? c - (1 << 21) : c;
        }

        /// <summary>Inverse of <see cref="PackId"/>.</summary>
        public static Vector3Int UnpackId(ulong id) => new Vector3Int(Signed(id >> 42), Signed(id >> 21), Signed(id));

        /// <summary>Which cell a position in this grid's own floating-origin frame falls in.</summary>
        public Vector3Int CoordOf(Vector3 framePosition)
        {
            var origin = Frame.Cell;
            return new Vector3Int(
                origin.x + Mathf.FloorToInt(framePosition.x / CellSize.x),
                Planar ? 0 : origin.y + Mathf.FloorToInt(framePosition.y / CellSize.y),
                origin.z + Mathf.FloorToInt(framePosition.z / CellSize.z));
        }

        /// <summary>
        /// Which cell an entity is in. For an entity directly in a chunk of this grid, it is the chunk's cell offset by
        /// the entity's local position in the chunk. Anywhere else, including aboard a carrier at any depth, it is the
        /// cell of the entity's position in its scope's own space (<see cref="NetworkIdentity.ToScope"/>). On a worker,
        /// the transform of an entity inside a physics frame reads frame-local coordinates, so the position is
        /// converted out through every frame: a rider is in the cell its carrier is in.
        /// </summary>
        public Vector3Int CoordOf(NetworkIdentity entity)
        {
            var container = entity.Container;
            // Only a root of this grid's scope is a chunk (docs/container-tree.md D9). The public grid unpacks any
            // id, so a runtime container of another kind (a room fixed in a frame, another scope's box) would
            // otherwise be read as a cell it is not.
            if (container == null || !container.IsRuntime || container.Parent != null || container.InstanceId != InstanceId
                || !TryCoordOf(container.RuntimeId, out var c))
                return CoordOf(entity.ToScope(entity.transform.position));
            var local = entity.LocalPosition;
            return new Vector3Int(
                c.x + Mathf.FloorToInt((local.x + CellSize.x / 2) / CellSize.x),
                Planar ? 0 : c.y + Mathf.FloorToInt((local.y + CellSize.y / 2) / CellSize.y),
                c.z + Mathf.FloorToInt((local.z + CellSize.z / 2) / CellSize.z));
        }

        /// <summary>
        /// Centre of a cell in the current floating-origin frame. A planar grid's column is centered on absolute
        /// y = 0 (the origin never shifts vertically in a planar world), so its box spans ±CellSize.y/2 around the
        /// ground plane rather than sitting above it.
        /// </summary>
        public Vector3 CenterOf(Vector3Int coord)
        {
            var origin = Frame.Cell;
            return new Vector3(
                (float)(((long)coord.x - origin.x + 0.5) * CellSize.x),
                Planar
                    ? (float)(-(long)origin.y * (double)CellSize.y)
                    : (float)(((long)coord.y - origin.y + 0.5) * CellSize.y),
                (float)(((long)coord.z - origin.z + 0.5) * CellSize.z));
        }

        /// <summary>Box of a cell in the current floating-origin frame.</summary>
        public Bounds BoundsOf(Vector3Int coord) => new Bounds(CenterOf(coord), CellSize);

        /// <summary><see cref="BoundsOf"/> of the cell a packed runtime id names. Matches the
        /// <see cref="ContainerRegistry.RuntimeBoundsInFrame"/> delegate signature; see <see cref="UseAsRuntimeBounds"/>.</summary>
        public Bounds BoundsOfId(ulong id, Bounds fallback) => TryCoordOf(id, out var coord) ? BoundsOf(coord) : fallback;

        /// <summary>Chebyshev (ring) distance between two coordinates, independent of cell size.</summary>
        public static bool IsNear(Vector3Int a, Vector3Int b, int ring) =>
            Math.Abs((long)a.x - b.x) <= ring && Math.Abs((long)a.y - b.y) <= ring && Math.Abs((long)a.z - b.z) <= ring;

        /// <summary>Every valid coordinate within <paramref name="ring"/> cells of <paramref name="center"/> on every axis (a cube).</summary>
        public static IEnumerable<Vector3Int> Neighborhood(Vector3Int center, int ring)
        {
            for (int x = -ring; x <= ring; x++)
                for (int y = -ring; y <= ring; y++)
                    for (int z = -ring; z <= ring; z++)
                    {
                        var c = center + new Vector3Int(x, y, z);
                        if (IsValid(c)) yield return c;
                    }
        }

        /// <summary>
        /// The neighborhood of this grid, appended to <paramref name="into"/>: a cube for a volumetric grid, a
        /// square in the y = 0 layer for a <see cref="Planar"/> one. Takes a list rather than yielding, because
        /// the allocator walks it every policy tick and an iterator would allocate an enumerator each time.
        /// </summary>
        public void Neighborhood(Vector3Int center, int ring, List<Vector3Int> into)
        {
            if (into == null) return;
            int yLow = Planar ? 0 : -ring, yHigh = Planar ? 0 : ring;
            int cy = Planar ? 0 : center.y;
            for (int x = -ring; x <= ring; x++)
                for (int y = yLow; y <= yHigh; y++)
                    for (int z = -ring; z <= ring; z++)
                    {
                        var c = new Vector3Int(center.x + x, cy + y, center.z + z);
                        if (IsValid(c)) into.Add(c);
                    }
        }

        /// <summary>Drop the vertical component of a coordinate when this grid is <see cref="Planar"/>, so a caller's arithmetic cannot leave the single layer.</summary>
        public Vector3Int Normalize(Vector3Int coord) => Planar ? new Vector3Int(coord.x, 0, coord.z) : coord;

        /// <summary>
        /// Point <see cref="ContainerRegistry.RuntimeBoundsInFrame"/> at this grid, so a game that registers runtime
        /// containers by <see cref="PackId"/>'d coordinate can use this grid as the bounds hook. Games
        /// with their own container shape keep using the hook directly; this is purely opt-in and touches nothing
        /// else. Call once at boot, and clear
        /// (<c>ContainerRegistry.RuntimeBoundsInFrame = null</c>) on shutdown if another hook should take over.
        /// </summary>
        public void UseAsRuntimeBounds() => ContainerRegistry.RuntimeBoundsInFrame = BoundsOfId;

        private static readonly List<CharacterController> Suspended = new List<CharacterController>();

        /// <summary>
        /// Opt-in floating-origin policy for a grid world: shift the origin to <paramref name="target"/> (a no-op
        /// when it is already there, or when no runtime world is loaded), suspending enabled
        /// <see cref="CharacterController"/>s on entities of runtime containers across the shift and syncing
        /// physics transforms after. PhysX controllers must be reinserted at the new pose, not sweep over the
        /// shift. Call from the game's own update once it has decided where the origin belongs, or use
        /// <see cref="KeepOriginNear"/> for the usual policy.
        /// </summary>
        public static void ShiftOriginTo(Vector3Int target)
        {
            if (NebulaWorld.Streamer == null || target == WorldOrigin.Cell) return;
            Suspend(0UL);
            NebulaWorld.Streamer.ShiftOrigin(target);
            Resume();
            Physics.SyncTransforms();
        }

        /// <summary>
        /// Move <b>this grid's</b> origin to <paramref name="target"/>. The public world's frame is the process's
        /// (the streamer moves the authored cell scenes with it), so that case is <see cref="ShiftOriginTo"/>
        /// unchanged. A scoped grid owns its frame: only that scope's containers, the entities in them, their state
        /// history and interpolation buffers move, and no other scope on this worker notices
        /// (<c>docs/scope-frames.md</c> D3).
        /// </summary>
        public void ShiftOrigin(Vector3Int target)
        {
            if (Planar) target = new Vector3Int(target.x, Frame.Cell.y, target.z);
            if (IsPublic) { ShiftOriginTo(target); return; }
            if (target == Frame.Cell) return;
            var delta = Frame.ShiftDelta(Frame.Cell, target);
            Suspend(InstanceId);
            // The frame moves first: ContainerRegistry asks the bounds hook, which recomputes every chunk of this
            // grid from its coordinate in the *new* frame rather than translating a box and letting it drift.
            Frame.Apply(target, delta);
            ContainerRegistry.ShiftRuntime(InstanceId, delta);
            ContainerRegistry.RefreshCaches();
            NetworkIdentity.ShiftFrameAll(InstanceId, delta);
            Resume();
            Physics.SyncTransforms();
        }

        /// <summary>
        /// PhysX controllers must be reinserted at the new pose rather than sweep over the shift, so every enabled
        /// <see cref="CharacterController"/> on an entity of the moving frame is switched off across it.
        /// </summary>
        private static void Suspend(ulong frameId)
        {
            Suspended.Clear();
            foreach (var c in ContainerRegistry.Runtime)
            {
                if (c == null || ScopeFrames.FrameIdOf(c.InstanceId) != frameId) continue;
                foreach (var entity in c.Entities)
                {
                    if (entity == null) continue;
                    var controller = entity.GetComponent<CharacterController>();
                    if (controller != null && controller.enabled) { controller.enabled = false; Suspended.Add(controller); }
                }
            }
        }

        private static void Resume()
        {
            foreach (var controller in Suspended) if (controller != null) controller.enabled = true;
            Suspended.Clear();
        }

        /// <summary>
        /// Keep the floating origin within <paramref name="ring"/> cells of <paramref name="entity"/> — the usual
        /// policy for a client following its local player. Shifts through <see cref="ShiftOriginTo"/> when the
        /// entity leaves that ring around <see cref="WorldOrigin.Cell"/>, and does nothing otherwise.
        /// </summary>
        public void KeepOriginNear(NetworkIdentity entity, int ring = 1)
        {
            if (entity == null) return;
            KeepOriginNear(CoordOf(entity), ring);
        }

        /// <summary>
        /// <see cref="KeepOriginNear(NetworkIdentity,int)"/> for a coordinate a role computed itself (a worker
        /// follows the centroid of the cells it leases, not any one entity). Planar grids never shift vertically,
        /// so the target keeps the current origin's y.
        /// </summary>
        public void KeepOriginNear(Vector3Int cell, int ring = 1)
        {
            var origin = Frame.Cell;
            if (Planar) cell = new Vector3Int(cell.x, origin.y, cell.z);
            if (!IsNear(cell, origin, ring)) ShiftOrigin(cell);
        }
    }
}
