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

        /// <summary>
        /// The key this grid is registered under (<see cref="NebulaChunks.GridFor"/>) and its chunk ids derive from
        /// (<see cref="IdOf"/>). A scope's root grid's is its <see cref="ScopeKey"/>; a hosted grid's is its own, distinct
        /// from the scope it lives in (<c>docs/container-tree.md</c> D22).
        /// </summary>
        public string GridKey { get; }

        /// <summary>
        /// The container a hosted grid's chunks are leased under, by id: a container with a physics frame of its own,
        /// typically a carrier's (a planet, a station). Null for a scope's root grid, whose chunks are roots.
        /// </summary>
        public string HostContainerId { get; }

        /// <summary>
        /// Whether this grid is hosted by a container (<c>docs/container-tree.md</c> D22) rather than being its scope's
        /// root grid: its chunks are children of <see cref="HostContainerId"/>, its coordinates are the host's own
        /// (carrier-local), and its <see cref="Frame"/> follows the host's floating origin.
        /// </summary>
        public bool IsHosted => HostContainerId != null;

        /// <summary>
        /// The grid's shape: which cell a point is in, each cell's box, its neighbours and its id. A root grid's is always
        /// a <see cref="ChunkLattice"/> of <see cref="CellSize"/>; a hosted grid's may be any
        /// <see cref="IChunkGridGeometry"/> a game supplies (<see cref="NebulaChunkedWorld.HostedGeometry"/>), and every
        /// hosted-grid answer below goes through it.
        /// </summary>
        public IChunkGridGeometry Geometry { get; }

        // A scoped grid's ids are a hash, so they cannot be unpacked. Both directions are memoised as coordinates
        // are named (by this process) or adopted from a lease row (by any other process); a chunk nobody has
        // mentioned costs nothing.
        private readonly Dictionary<Vector3Int, ulong> _idByCoord;
        private readonly Dictionary<ulong, Vector3Int> _coordById;

        public RuntimeGrid(float cellSize) : this(new Vector3(cellSize, cellSize, cellSize), false) { }

        public RuntimeGrid(Vector3 cellSize) : this(cellSize, false) { }

        public RuntimeGrid(Vector3 cellSize, bool planar) : this(cellSize, planar, "") { }

        public RuntimeGrid(Vector3 cellSize, bool planar, string scopeKey) : this(cellSize, planar, scopeKey, null, null) { }

        private RuntimeGrid(Vector3 cellSize, bool planar, string scopeKey, string gridKey, string hostContainerId, IChunkGridGeometry geometry = null)
        {
            if (cellSize.x <= 0f || cellSize.y <= 0f || cellSize.z <= 0f)
                throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive on every axis.");
            CellSize = cellSize;
            Geometry = geometry ?? new ChunkLattice(cellSize, planar);
            // A geometry of the game's own is not a lattice of columns, whatever the definition says.
            Planar = Geometry is ChunkLattice lattice ? lattice.Planar : false;
            ScopeKey = scopeKey ?? "";
            GridKey = string.IsNullOrEmpty(gridKey) ? ScopeKey : gridKey;
            HostContainerId = string.IsNullOrEmpty(hostContainerId) ? null : hostContainerId;
            InstanceId = ScopeKey.Length == 0 ? 0UL : ScopeKeys.Hash(ScopeKey);
            if (InstanceId == 0) return;
            _idByCoord = new Dictionary<Vector3Int, ulong>();
            _coordById = new Dictionary<ulong, Vector3Int>();
        }

        /// <summary>The grid a <see cref="ChunkGridDefinition"/> describes, for the scope it was activated under.</summary>
        public static RuntimeGrid From(ChunkGridDefinition definition, string scopeKey) =>
            definition == null ? null : new RuntimeGrid(definition.CellSize, definition.Planar, scopeKey);

        /// <summary>
        /// A grid hosted by a container (<c>docs/container-tree.md</c> D22): chunks of <paramref name="definition"/>'s
        /// size leased as children of <paramref name="hostContainerId"/>, laid out in the host's own coordinates, in
        /// scope <paramref name="scopeKey"/>, with ids derived from <paramref name="gridKey"/> and the coordinate. A
        /// planar one is a single layer of columns centred on the host's y = 0. The scope key must not be empty (the
        /// public world hosts no grids) and the grid key must not be any scope's key. <paramref name="geometry"/> replaces
        /// the lattice the definition describes with a shape of the game's own (a cube-sphere's faces); its cell size
        /// then only sizes the grid's frame.
        /// <see cref="NebulaChunkedWorld.ActivateHostedGrid(IControlPlane, string, string, string, ChunkGridDefinition)"/>
        /// makes one on every worker; build one directly only for a custom allocation workflow.
        /// </summary>
        public static RuntimeGrid Hosted(ChunkGridDefinition definition, string scopeKey, string gridKey, string hostContainerId, IChunkGridGeometry geometry = null)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (string.IsNullOrEmpty(scopeKey)) throw new ArgumentException("A hosted grid lives in a scope; the public world hosts none.", nameof(scopeKey));
            if (string.IsNullOrEmpty(gridKey)) throw new ArgumentException("A hosted grid needs a key of its own.", nameof(gridKey));
            if (string.IsNullOrEmpty(hostContainerId)) throw new ArgumentException("A hosted grid needs the id of the container that hosts it.", nameof(hostContainerId));
            return new RuntimeGrid(definition.CellSize, definition.Planar, scopeKey, gridKey, hostContainerId, geometry);
        }

        private Container _host;
        private PhysicsFrame _hostFrame;

        /// <summary>
        /// A hosted grid's host container in this process, or null while it is not here (a carrier not spawned or not
        /// yet seen) or for a root grid. A planar grid makes its host's frame keep its origin level
        /// (<see cref="PhysicsFrame.KeepOriginLevel"/>), and the frame's origin shifts are this grid's
        /// (<see cref="ScopeFrame.Shifted"/>).
        /// </summary>
        public Container Host
        {
            get
            {
                if (HostContainerId == null) return null;
                if (_host == null || _host.ContainerId != HostContainerId) _host = ContainerRegistry.FindById(HostContainerId);
                var frame = _host != null ? _host.Frame : null;
                if (frame != _hostFrame)
                {
                    if (_hostFrame != null) _hostFrame.Shifted -= OnHostShifted;
                    _hostFrame = frame;
                    if (frame != null)
                    {
                        frame.Shifted += OnHostShifted;
                        if (Planar) frame.KeepOriginLevel = true;
                    }
                }
                return _host;
            }
        }

        private void OnHostShifted(Vector3 delta) => _frame?.RaiseFollowed(delta);

        /// <summary>Where the host's (0,0,0) sits in this process's simulation space: its frame's floating origin taken off.</summary>
        private Vector3 HostOffset()
        {
            var host = Host;
            return host != null && host.Frame != null ? host.Frame.RootOffset : Vector3.zero;
        }

        /// <summary>
        /// A hosted grid: the position of <paramref name="entity"/> in the host's own coordinates, wherever it is (in a
        /// chunk of this grid, in the host's frame at any depth, or outside the host altogether). False when the host is
        /// not in this process, or for a root grid.
        /// </summary>
        public bool TryHostPosition(NetworkIdentity entity, out Vector3 local)
        {
            local = default;
            var host = entity != null ? Host : null;
            if (host == null) return false;
            var p = PhysicsFrames.Convert(entity.transform.position, entity.Space, host);
            local = host.Frame != null ? host.Frame.SimulationToLocal(p) : p;
            return true;
        }

        /// <summary>
        /// Which cell a position in this grid's absolute coordinates falls in: the scope's absolute world for a root
        /// grid, the host's own coordinates for a hosted one. No floating origin enters into it.
        /// </summary>
        public Vector3Int CoordOfAbsolute(Vector3 absolute) => Geometry.CellOf(absolute);

        /// <summary>
        /// Centre of a cell in this grid's absolute coordinates (the host's own, for a hosted grid). A planar grid's
        /// column is centred on y = 0.
        /// </summary>
        public Vector3 AbsoluteCenterOf(Vector3Int coord) => Geometry.BoundsOf(Normalize(coord)).center;

        /// <summary>A cell's box in this grid's absolute coordinates (the host's own, for a hosted grid).</summary>
        public Bounds AbsoluteBoundsOf(Vector3Int coord) => Geometry.BoundsOf(Normalize(coord));

        /// <summary>How far apart the allocator samples a pawn's lead line (<see cref="IChunkGridGeometry.LeadStep"/>).</summary>
        public float LeadStep => Geometry.LeadStep;

        /// <summary>
        /// The placement a chunk's lease row carries: a root at its absolute centre (in double) for a root grid, a
        /// leased child of the host at its centre in the host's coordinates for a hosted one.
        /// </summary>
        public ContainerPlacement PlacementOf(Vector3Int coord)
        {
            coord = Normalize(coord);
            if (IsHosted)
            {
                var box = Geometry.BoundsOf(coord);
                return ContainerPlacement.Child(HostContainerId, box.center, box.size, ContainerAuthority.Leased);
            }
            var center = new Double3((coord.x + 0.5) * CellSize.x, Planar ? 0.0 : (coord.y + 0.5) * CellSize.y, (coord.z + 0.5) * CellSize.z);
            return ContainerPlacement.Root(center, CellSize);
        }

        /// <summary>The part id a chunk's lease row and its records carry: <c>c/x/y/z</c>, after the grid key for a hosted grid.</summary>
        public string PartIdOf(Vector3Int coord) =>
            IsHosted ? ChunkKeys.HostedPartId(GridKey, Normalize(coord)) : ChunkKeys.PartId(Normalize(coord));

        private ScopeFrame _frame;

        /// <summary>
        /// This grid's floating-origin frame: <see cref="WorldOrigin"/> for the public world, and a frame of its own
        /// for every other scope. Every piece of arithmetic below is relative to it, which is what lets two scopes on
        /// one worker both sit near Unity's origin (<c>docs/scope-frames.md</c>). A hosted grid's frame follows its host's
        /// floating origin (<see cref="ScopeFrame.IsFollowing"/>): its <see cref="ScopeFrame.OriginOffset"/> turns the host's
        /// own coordinates into simulation space and back, on a worker and on a client while it predicts; a client
        /// draws the host where it is (<see cref="PhysicsFrame.ToParent(Vector3)"/>).
        /// </summary>
        public ScopeFrame Frame => _frame ??= IsHosted ? new ScopeFrame(ScopeKey, InstanceId, CellSize, HostOffset)
            : IsPublic ? ScopeFrames.Public : ScopeFrames.Ensure(ScopeKey, InstanceId, CellSize);

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
            id = IsHosted ? Geometry.IdOf(GridKey, coord) : ChunkKeys.RuntimeId(GridKey, coord);
            _idByCoord[coord] = id;
            _coordById[id] = coord;
            return id;
        }

        /// <summary>This grid's control-plane container id for a chunk (<c>rt_</c> and <see cref="IdOf"/>).</summary>
        public string ContainerIdOf(Vector3Int coord) => ContainerRegistry.RuntimeContainerId(IdOf(coord));

        /// <summary>
        /// The chunk an id names in this grid, or false when the id is not this grid's. Always true for the public
        /// grid (every 64-bit value unpacks to a coordinate); for a scoped grid it is true for a chunk this process
        /// has named or adopted from a lease row (<see cref="Adopt"/>). The overload that takes a
        /// <see cref="PersistedEntityRecord"/> places a saved entity from its record's part id
        /// (<see cref="PersistedEntityRecord.PartId"/>), adopting the id as it goes, so it works in a process that never
        /// named the chunk; it is false for a record of another scope, in no runtime container, or inside a carrier.
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
            Vector3Int parsed;
            if (IsHosted)
            {
                if (!ChunkKeys.TryParseHostedPartId(partId, out var key, out parsed) || !string.Equals(key, GridKey, StringComparison.Ordinal)) return false;
            }
            else if (!ChunkKeys.TryParsePartId(partId, out parsed)) return false;
            parsed = Normalize(parsed);
            if (_coordById == null) { coord = parsed; return PackId(parsed) == id; }
            if ((IsHosted ? Geometry.IdOf(GridKey, parsed) : ChunkKeys.RuntimeId(GridKey, parsed)) != id) return false;
            _idByCoord[parsed] = id;
            _coordById[id] = parsed;
            coord = parsed;
            return true;
        }

        /// <summary>
        /// The chunk of this grid a persisted record was saved in, from the record alone. False when the record is in
        /// another scope (<see cref="PersistedEntityRecord.ScopeKey"/> differs from <see cref="ScopeKey"/>), in no
        /// runtime container, or inside a carrier. A record that carries its chunk's part id
        /// (<see cref="PersistedEntityRecord.PartId"/>) is placed by <see cref="Adopt"/>, so the grid learns the id
        /// too and later <see cref="TryCoordOf(ulong, out Vector3Int)"/> calls for it succeed. A record without one
        /// is placed only when this grid already knows the id: always in the public world, and in a scoped grid
        /// when this process has named or adopted the chunk. For an older scoped record whose part is unknown, use
        /// <see cref="TryFindCoordNear"/> with an approximate position.
        /// </summary>
        public bool TryCoordOf(PersistedEntityRecord record, out Vector3Int coord)
        {
            coord = default;
            if (record == null || !string.IsNullOrEmpty(record.CarrierKey)) return false;
            if (!string.Equals(record.ScopeKey ?? "", ScopeKey, StringComparison.Ordinal)) return false;
            if (!ChunkKeys.TryParseContainerId(record.ContainerId, out ulong id)) return false;
            if (!string.IsNullOrEmpty(record.PartId) && Adopt(id, record.PartId, out coord)) return true;
            return TryCoordOf(id, out coord);
        }

        /// <summary>
        /// Find the chunk of this grid named by <paramref name="id"/> by trying every coordinate within
        /// <paramref name="radius"/> cells of <paramref name="around"/>: a square in the y = 0 layer for a
        /// <see cref="Planar"/> grid, a cube otherwise. Use it for a scoped chunk id this process never named and no
        /// record or lease row explains, such as a record saved before <see cref="PersistedEntityRecord.PartId"/>
        /// existed, when the game keeps an approximate position for it. On a match the grid learns the id, as
        /// <see cref="Adopt"/> does.
        /// <para>
        /// The search goes outward ring by ring and stops at the first match. It computes one id per coordinate:
        /// (2r+1)² for a planar grid and (2r+1)³ for a volumetric one, each a short string hash, so keep the radius to
        /// what the hint's error needs. An id the grid already knows is answered from what it knows, wherever the
        /// chunk is; the public grid unpacks the id and reports whether the chunk is within the radius. False for a
        /// negative radius, or when no coordinate in range matches.
        /// </para>
        /// </summary>
        public bool TryFindCoordNear(ulong id, Vector3Int around, int radius, out Vector3Int coord)
        {
            coord = default;
            if (radius < 0) return false;
            around = Normalize(around);
            if (_coordById == null)
            {
                coord = Normalize(UnpackId(id));
                return IsNear(coord, around, radius);
            }
            if (_coordById.TryGetValue(id, out coord)) return true;
            int yReach = Planar ? 0 : radius;
            for (int ring = 0; ring <= radius; ring++)
            {
                int dyMax = Math.Min(ring, yReach);
                for (int dx = -ring; dx <= ring; dx++)
                    for (int dy = -dyMax; dy <= dyMax; dy++)
                        for (int dz = -ring; dz <= ring; dz++)
                        {
                            // Only the shell of this ring: the cells inside it were tried by the smaller rings.
                            if (Math.Abs(dx) != ring && Math.Abs(dy) != ring && Math.Abs(dz) != ring) continue;
                            long x = (long)around.x + dx, y = (long)around.y + dy, z = (long)around.z + dz;
                            if (x < MinCoordinate || x > MaxCoordinate || y < MinCoordinate || y > MaxCoordinate ||
                                z < MinCoordinate || z > MaxCoordinate) continue;
                            var c = new Vector3Int((int)x, (int)y, (int)z);
                            if ((IsHosted ? Geometry.IdOf(GridKey, c) : ChunkKeys.RuntimeId(GridKey, c)) != id) continue;
                            _idByCoord[c] = id;
                            _coordById[id] = c;
                            coord = c;
                            return true;
                        }
            }
            coord = default;
            return false;
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
            if (IsHosted) return CoordOfAbsolute(framePosition - HostOffset());
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
            // Only a root of this grid's scope is a chunk (docs/container-tree.md D9), and for a hosted grid only a child
            // of its host (D22). The public grid unpacks any id, so a runtime container of another kind (a room fixed in
            // a frame, another scope's box) would otherwise be read as a cell it is not.
            bool chunk = container != null && container.IsRuntime && container.InstanceId == InstanceId
                && (IsHosted ? container.Parent != null && container.Parent.ContainerId == HostContainerId : container.Parent == null);
            if (!chunk || !TryCoordOf(container.RuntimeId, out var c))
            {
                if (!IsHosted) return CoordOf(entity.ToScope(entity.transform.position));
                return TryHostPosition(entity, out var hostPosition) ? CoordOfAbsolute(hostPosition) : CoordOf(entity.transform.position);
            }
            // In a hosted chunk: the chunk's place in its host plus the entity's place in the chunk, asked of the geometry,
            // which alone knows where one cell ends and the next begins.
            if (IsHosted) return Geometry.CellOf(container.transform.localPosition + entity.LocalPosition);
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
            if (IsHosted) return AbsoluteCenterOf(Normalize(coord)) + HostOffset();
            var origin = Frame.Cell;
            return new Vector3(
                (float)(((long)coord.x - origin.x + 0.5) * CellSize.x),
                Planar
                    ? (float)(-(long)origin.y * (double)CellSize.y)
                    : (float)(((long)coord.y - origin.y + 0.5) * CellSize.y),
                (float)(((long)coord.z - origin.z + 0.5) * CellSize.z));
        }

        /// <summary>Box of a cell in the current floating-origin frame.</summary>
        public Bounds BoundsOf(Vector3Int coord) => IsHosted ? new Bounds(CenterOf(coord), Geometry.BoundsOf(Normalize(coord)).size) : new Bounds(CenterOf(coord), CellSize);

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
            if (IsHosted) { Geometry.Neighborhood(center, ring, into); return; }
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
        public Vector3Int Normalize(Vector3Int coord) => IsHosted ? Geometry.Normalize(coord) : Planar ? new Vector3Int(coord.x, 0, coord.z) : coord;

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
        /// <see cref="KeepOriginNear"/> for the usual policy. On a client the physics frames are posed again at once
        /// (<see cref="PhysicsFrames.PoseForRender()"/>), so what stands in them moves with the origin.
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
        /// (<c>docs/scope-frames.md</c> D3). What stands inside a physics frame keeps its simulation pose, which is the
        /// frame's (<c>docs/container-tree.md</c> D19); on a client the frames are posed again at once
        /// (<see cref="PhysicsFrames.PoseForRender()"/>), so what stands in them is drawn with the origin.
        /// </summary>
        public void ShiftOrigin(Vector3Int target)
        {
            // A hosted grid has no origin of its own to move: it follows its host's frame (docs/container-tree.md D19, D22).
            if (IsHosted) return;
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
            // A client draws its physics frames posed at their carriers (docs/container-tree.md D11): the carriers just
            // moved, so the frames, and everything standing in them, move now (controllers still suspended), not at the
            // client's next render pose. An origin rule run later this frame reads them where the origin put them.
            PhysicsFrames.PoseForRender();
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
            if (IsHosted) return;
            var origin = Frame.Cell;
            if (Planar) cell = new Vector3Int(cell.x, origin.y, cell.z);
            if (!IsNear(cell, origin, ring)) ShiftOrigin(cell);
        }
    }
}
