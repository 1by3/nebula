using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// A uniform spatial hash over <b>absolute</b> world coordinates: the region arithmetic every part of interest
    /// management shares (gateway, worker, standalone services). One region is one cell of the grid; its id is the
    /// pinned three-axis 21-bit packing <c>RuntimeGrid.PackId</c> uses, so a chunked world's region ids and its
    /// container ids are literally the same numbers.
    /// <para>
    /// Positions are doubles because a region key must not depend on the floating origin: wire poses are
    /// container-local, and both ends add the container's absolute center in double before asking for a key. Shift
    /// the origin and every key stays what it was, which is what lets a gateway and a worker with different origins
    /// agree on one set of subscribed regions.
    /// </para>
    /// <para>
    /// The packing is duplicated here rather than called: <c>RuntimeGrid</c> lives in the Unity-only
    /// <c>Nebula.World</c> assembly, which the standalone gateway does not compile. <c>InterestPackingPinTests</c>
    /// asserts the two agree for sample coordinates, and neither may change: ids are persisted.
    /// </para>
    /// <para>
    /// <b>The far band.</b> 21 bits of edge-sized regions reach ±2^20 edges from the origin (±67,108 km at 64 m),
    /// which a star system outgrows. The packing uses 63 bits, so the top bit marks a <i>far region</i>: the same
    /// three 21-bit fields counting cells <see cref="FarScale"/> edges wide (2,048 m at 64 m, so ±2,147,483 km).
    /// A position inside the near cube [<see cref="MinCoordinate"/>, <see cref="MaxCoordinate"/>] on every axis gets
    /// exactly the key it always had; a position outside it gets a far key. The near cube's faces lie on far-cell
    /// edges, so the two bands partition space with no overlap, and a far key for a cell inside the near cube is
    /// never made. Both ends derive the band from the grid alone, so no setting, message or protocol version changed.
    /// </para>
    /// </summary>
    public readonly struct InterestGrid : IEquatable<InterestGrid>
    {
        /// <summary>Coordinates outside [<see cref="MinCoordinate"/>, <see cref="MaxCoordinate"/>] cannot be packed; collectors clamp to this range.</summary>
        public const int MinCoordinate = -(1 << 20);
        public const int MaxCoordinate = (1 << 20) - 1;
        private const ulong Mask = (1UL << 21) - 1;
        /// <summary>A far region is this many edges wide on each axis. Never change this: both ends must agree.</summary>
        public const int FarScale = 32;
        /// <summary>The bit that marks a far region's key (<see cref="IsFar"/>); the plain packing never sets it.</summary>
        public const ulong FarBit = 1UL << 63;
        private const int FarMin = MinCoordinate / FarScale, FarMax = (MaxCoordinate + 1) / FarScale - 1;

        /// <summary>Whether a (plain, unsalted) region key is a far region, outside the near cube.</summary>
        public static bool IsFar(ulong region) => (region & FarBit) != 0;

        /// <summary>Region edge in meters per axis. Y is unused (and meaningless) when <see cref="Planar"/>.</summary>
        public readonly double EdgeX, EdgeY, EdgeZ;
        /// <summary>World coordinate of the region-0 lower corner per axis; see <see cref="Resolve(in InterestSettings,double,double,double,bool)"/>.</summary>
        public readonly double OffsetX, OffsetY, OffsetZ;
        /// <summary>Regions are infinite columns: the Y coordinate of every region is 0 and Y never enters a key.</summary>
        public readonly bool Planar;

        public InterestGrid(double edge, bool planar = true) : this(edge, edge, edge, 0, 0, 0, planar) { }

        public InterestGrid(double edgeX, double edgeY, double edgeZ, double offsetX, double offsetY, double offsetZ, bool planar)
        {
            EdgeX = edgeX; EdgeY = edgeY; EdgeZ = edgeZ;
            OffsetX = offsetX; OffsetY = offsetY; OffsetZ = offsetZ;
            Planar = planar;
        }

        /// <summary>A grid with a positive edge on every axis it uses. A default-constructed grid is not valid.</summary>
        public bool IsValid => EdgeX > 0 && EdgeZ > 0 && (Planar || EdgeY > 0);

        /// <summary>
        /// Derive the grid both ends must agree on from the resolved settings and, when the game has a world
        /// definition, its cell size. The edge is snapped to an integer division of the cell so region
        /// edges coincide with cell edges, and the offset places region boundaries on those cell edges. Baked cells
        /// are centered on <c>coord × CellSize</c> (<paramref name="cellsCentred"/>: offset −CellSize/2);
        /// <c>RuntimeGrid</c> cells start at <c>coord × CellSize</c> (offset 0). Pass 0 for no world definition.
        /// </summary>
        public static InterestGrid Resolve(in InterestSettings settings, double worldCellX, double worldCellY, double worldCellZ, bool cellsCentred)
        {
            double wanted = settings.CellSize > 0 ? settings.CellSize : InterestSettings.Default.CellSize;
            Axis(wanted, worldCellX, cellsCentred, out double ex, out double ox);
            Axis(wanted, worldCellY, cellsCentred, out double ey, out double oy);
            Axis(wanted, worldCellZ, cellsCentred, out double ez, out double oz);
            return new InterestGrid(ex, ey, ez, ox, oy, oz, settings.Planar);
        }

        public static InterestGrid Resolve(in InterestSettings settings, double worldCellSize = 0, bool cellsCentred = false) =>
            Resolve(settings, worldCellSize, worldCellSize, worldCellSize, cellsCentred);

        private static void Axis(double wanted, double cell, bool centred, out double edge, out double offset)
        {
            if (cell <= 0) { edge = wanted; offset = 0; return; }
            double divisions = Math.Max(1, Math.Round(cell / wanted, MidpointRounding.AwayFromZero));
            edge = cell / divisions;
            offset = centred ? -cell / 2 : 0;
        }

        /// <summary>The snapped edge the settings ask for, before a key is ever made; for logging and validation.</summary>
        public static double SnapEdge(double wanted, double cell) { Axis(wanted, cell, false, out double edge, out _); return edge; }

        // ------------------------------------------------------------------------------------------- keys

        /// <summary>Pack a region coordinate: three signed 21-bit fields, x in the high bits. Never change this.</summary>
        public static ulong PackRegion(int x, int y, int z) =>
            (((ulong)(uint)x & Mask) << 42) | (((ulong)(uint)y & Mask) << 21) | ((ulong)(uint)z & Mask);

        public static void UnpackRegion(ulong region, out int x, out int y, out int z)
        {
            x = Signed(region >> 42); y = Signed(region >> 21); z = Signed(region);
        }

        private static int Signed(ulong value)
        {
            int c = (int)(value & Mask);
            return c >= (1 << 20) ? c - (1 << 21) : c;
        }

        private static int Clamp(long c) => c < MinCoordinate ? MinCoordinate : c > MaxCoordinate ? MaxCoordinate : (int)c;

        private static bool Near(long c) => c >= MinCoordinate && c <= MaxCoordinate;
        private static long FloorDiv(long c, long d) => c >= 0 ? c / d : -((-c + d - 1) / d);
        private static bool NearFar(long c) => c >= FarMin && c <= FarMax;

        // Unclamped edge-sized coordinates. The double-to-long cast saturates far beyond any meaningful distance.
        private long RawX(double x) => (long)Math.Floor((x - OffsetX) / EdgeX);
        private long RawY(double y) => Planar ? 0 : (long)Math.Floor((y - OffsetY) / EdgeY);
        private long RawZ(double z) => (long)Math.Floor((z - OffsetZ) / EdgeZ);

        private int CoordX(double x) => Clamp(RawX(x));
        private int CoordY(double y) => Planar ? 0 : Clamp(RawY(y));
        private int CoordZ(double z) => Clamp(RawZ(z));

        private static ulong KeyOf(long cx, long cy, long cz)
        {
            if (Near(cx) && Near(cy) && Near(cz)) return PackRegion((int)cx, (int)cy, (int)cz);
            return FarBit | PackRegion(Clamp(FloorDiv(cx, FarScale)), Clamp(FloorDiv(cy, FarScale)), Clamp(FloorDiv(cz, FarScale)));
        }

        /// <summary>
        /// The only place a region key is made. Absolute world position in, packed region id out: a near region inside
        /// ±2^20 edges of the origin on every axis, a far region (<see cref="IsFar"/>) outside it.
        /// </summary>
        public ulong RegionOf(double x, double y, double z) => KeyOf(RawX(x), RawY(y), RawZ(z));

        /// <summary>Near-band region coordinate of an absolute world position, clamped to the near cube (Y is always 0 on a planar grid).</summary>
        public void CoordOf(double x, double y, double z, out int cx, out int cy, out int cz)
        {
            cx = CoordX(x); cy = CoordY(y); cz = CoordZ(z);
        }

        /// <summary>
        /// The region's box in absolute world coordinates. A planar grid's regions are columns, so Y spans
        /// ±infinity: a caller measuring a distance to the box gets the column distance it wants.
        /// </summary>
        public void BoundsOf(ulong region, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ)
        {
            UnpackRegion(region & ~FarBit, out int x, out int y, out int z);
            double s = IsFar(region) ? FarScale : 1;
            minX = OffsetX + x * s * EdgeX; maxX = minX + s * EdgeX;
            minZ = OffsetZ + z * s * EdgeZ; maxZ = minZ + s * EdgeZ;
            if (Planar) { minY = double.NegativeInfinity; maxY = double.PositiveInfinity; }
            else { minY = OffsetY + y * s * EdgeY; maxY = minY + s * EdgeY; }
        }

        /// <summary>Center of a region in absolute world coordinates (Y = 0 on a planar grid, where there is no center).</summary>
        public void CenterOf(ulong region, out double x, out double y, out double z)
        {
            BoundsOf(region, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ);
            x = (minX + maxX) / 2;
            y = Planar ? 0 : (minY + maxY) / 2;
            z = (minZ + maxZ) / 2;
        }

        /// <summary>Squared distance from a point to a region's box; 0 inside. Planar grids measure in the XZ plane only.</summary>
        public double SqrDistanceToRegion(ulong region, double x, double y, double z)
        {
            BoundsOf(region, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ);
            double dx = x < minX ? minX - x : x > maxX ? x - maxX : 0;
            double dz = z < minZ ? minZ - z : z > maxZ ? z - maxZ : 0;
            double dy = Planar ? 0 : y < minY ? minY - y : y > maxY ? y - maxY : 0;
            return dx * dx + dy * dy + dz * dz;
        }

        // ------------------------------------------------------------------------------------------- collection

        /// <summary>
        /// Append every region whose box is within <paramref name="radius"/> of the point. Each region appears once
        /// per call; a caller unioning several foci deduplicates (a <c>HashSet</c> or the subscription set itself).
        /// </summary>
        public void CollectDisc(double x, double y, double z, double radius, List<ulong> into)
        {
            if (into == null || !IsValid || radius < 0) return;
            Collect(RawX(x - radius), RawY(y - radius), RawZ(z - radius), RawX(x + radius), RawY(y + radius), RawZ(z + radius),
                true, x, y, z, radius * radius, into);
        }

        /// <summary>Append every region overlapping an absolute box (an <c>ObservePublic</c> window, a container).</summary>
        public void CollectBox(double minX, double minY, double minZ, double maxX, double maxY, double maxZ, List<ulong> into)
        {
            if (into == null || !IsValid) return;
            Collect(RawX(Math.Min(minX, maxX)), RawY(Math.Min(minY, maxY)), RawZ(Math.Min(minZ, maxZ)),
                RawX(Math.Max(minX, maxX)), RawY(Math.Max(minY, maxY)), RawZ(Math.Max(minZ, maxZ)), false, 0, 0, 0, 0, into);
        }

        /// <summary>
        /// Every region overlapping the edge-coordinate box [x0..x1] x [y0..y1] x [z0..z1]: the near regions of its
        /// part inside the near cube, then the far regions of its part outside it (a far cell wholly inside the near
        /// cube is never a key). With <paramref name="disc"/>, only regions within the radius of the point.
        /// </summary>
        private void Collect(long x0, long y0, long z0, long x1, long y1, long z1, bool disc, double px, double py, double pz, double r2, List<ulong> into)
        {
            bool anyNear = x1 >= MinCoordinate && x0 <= MaxCoordinate && y1 >= MinCoordinate && y0 <= MaxCoordinate && z1 >= MinCoordinate && z0 <= MaxCoordinate;
            if (anyNear)
            {
                int nx0 = Clamp(x0), nx1 = Clamp(x1), ny0 = Clamp(y0), ny1 = Clamp(y1), nz0 = Clamp(z0), nz1 = Clamp(z1);
                for (int cx = nx0; cx <= nx1; cx++)
                    for (int cy = ny0; cy <= ny1; cy++)
                        for (int cz = nz0; cz <= nz1; cz++)
                        {
                            ulong region = PackRegion(cx, cy, cz);
                            if (!disc || SqrDistanceToRegion(region, px, py, pz) <= r2) into.Add(region);
                        }
            }
            if (Near(x0) && Near(x1) && Near(y0) && Near(y1) && Near(z0) && Near(z1)) return;
            int fx0 = Clamp(FloorDiv(x0, FarScale)), fx1 = Clamp(FloorDiv(x1, FarScale));
            int fy0 = Clamp(FloorDiv(y0, FarScale)), fy1 = Clamp(FloorDiv(y1, FarScale));
            int fz0 = Clamp(FloorDiv(z0, FarScale)), fz1 = Clamp(FloorDiv(z1, FarScale));
            for (int cx = fx0; cx <= fx1; cx++)
                for (int cy = fy0; cy <= fy1; cy++)
                    for (int cz = fz0; cz <= fz1; cz++)
                    {
                        if (NearFar(cx) && NearFar(cy) && NearFar(cz)) continue;
                        ulong region = FarBit | PackRegion(cx, cy, cz);
                        if (!disc || SqrDistanceToRegion(region, px, py, pz) <= r2) into.Add(region);
                    }
        }

        // ------------------------------------------------------------------------------------------- identity

        /// <summary>
        /// Whether two grids would produce the same keys. The subscribe message carries the grid so the worker can
        /// reject a mismatch loudly instead of filtering with ids the gateway never meant; the tolerance
        /// absorbs the f32 round trip of that message, nothing more.
        /// </summary>
        public bool Matches(in InterestGrid other, double tolerance = 1e-3)
        {
            if (Planar != other.Planar) return false;
            if (Math.Abs(EdgeX - other.EdgeX) > tolerance || Math.Abs(EdgeZ - other.EdgeZ) > tolerance) return false;
            if (!Planar && Math.Abs(EdgeY - other.EdgeY) > tolerance) return false;
            if (Math.Abs(OffsetX - other.OffsetX) > tolerance || Math.Abs(OffsetZ - other.OffsetZ) > tolerance) return false;
            return Planar || Math.Abs(OffsetY - other.OffsetY) <= tolerance;
        }

        public bool Equals(InterestGrid other) => EdgeX == other.EdgeX && EdgeY == other.EdgeY && EdgeZ == other.EdgeZ &&
            OffsetX == other.OffsetX && OffsetY == other.OffsetY && OffsetZ == other.OffsetZ && Planar == other.Planar;
        public override bool Equals(object obj) => obj is InterestGrid g && Equals(g);
        public override int GetHashCode() => HashCode.Combine(EdgeX, EdgeY, EdgeZ, OffsetX, OffsetY, OffsetZ, Planar);
        public override string ToString() => $"grid({EdgeX}x{EdgeY}x{EdgeZ} @ {OffsetX},{OffsetY},{OffsetZ}{(Planar ? ", planar" : "")})";
    }
}
