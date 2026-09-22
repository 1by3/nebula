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
    /// </summary>
    public readonly struct InterestGrid : IEquatable<InterestGrid>
    {
        /// <summary>Coordinates outside [<see cref="MinCoordinate"/>, <see cref="MaxCoordinate"/>] cannot be packed; collectors clamp to this range.</summary>
        public const int MinCoordinate = -(1 << 20);
        public const int MaxCoordinate = (1 << 20) - 1;
        private const ulong Mask = (1UL << 21) - 1;

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

        private int CoordX(double x) => Clamp((long)Math.Floor((x - OffsetX) / EdgeX));
        private int CoordY(double y) => Planar ? 0 : Clamp((long)Math.Floor((y - OffsetY) / EdgeY));
        private int CoordZ(double z) => Clamp((long)Math.Floor((z - OffsetZ) / EdgeZ));

        /// <summary>The only place a region key is made. Absolute world position in, packed region id out.</summary>
        public ulong RegionOf(double x, double y, double z) => PackRegion(CoordX(x), CoordY(y), CoordZ(z));

        /// <summary>Region coordinate of an absolute world position (Y is always 0 on a planar grid).</summary>
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
            UnpackRegion(region, out int x, out int y, out int z);
            minX = OffsetX + x * EdgeX; maxX = minX + EdgeX;
            minZ = OffsetZ + z * EdgeZ; maxZ = minZ + EdgeZ;
            if (Planar) { minY = double.NegativeInfinity; maxY = double.PositiveInfinity; }
            else { minY = OffsetY + y * EdgeY; maxY = minY + EdgeY; }
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
            int x0 = CoordX(x - radius), x1 = CoordX(x + radius);
            int z0 = CoordZ(z - radius), z1 = CoordZ(z + radius);
            int y0 = Planar ? 0 : CoordY(y - radius), y1 = Planar ? 0 : CoordY(y + radius);
            double r2 = radius * radius;
            for (int cx = x0; cx <= x1; cx++)
                for (int cy = y0; cy <= y1; cy++)
                    for (int cz = z0; cz <= z1; cz++)
                    {
                        ulong region = PackRegion(cx, cy, cz);
                        if (SqrDistanceToRegion(region, x, y, z) <= r2) into.Add(region);
                    }
        }

        /// <summary>Append every region overlapping an absolute box (an <c>ObservePublic</c> window, a container).</summary>
        public void CollectBox(double minX, double minY, double minZ, double maxX, double maxY, double maxZ, List<ulong> into)
        {
            if (into == null || !IsValid) return;
            int x0 = CoordX(Math.Min(minX, maxX)), x1 = CoordX(Math.Max(minX, maxX));
            int z0 = CoordZ(Math.Min(minZ, maxZ)), z1 = CoordZ(Math.Max(minZ, maxZ));
            int y0 = Planar ? 0 : CoordY(Math.Min(minY, maxY)), y1 = Planar ? 0 : CoordY(Math.Max(minY, maxY));
            for (int cx = x0; cx <= x1; cx++)
                for (int cy = y0; cy <= y1; cy++)
                    for (int cz = z0; cz <= z1; cz++)
                        into.Add(PackRegion(cx, cy, cz));
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
