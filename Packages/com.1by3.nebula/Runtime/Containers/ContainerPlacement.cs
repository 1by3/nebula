using System;
using System.Globalization;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// Who simulates what is inside a container (<c>docs/container-tree.md</c> D6).
    /// </summary>
    public enum ContainerAuthority : byte
    {
        /// <summary>
        /// Leased when the container's frame is fixed, inherited when an entity drives it: what baked, runtime and
        /// carried containers always did. The default.
        /// </summary>
        Auto = 0,
        /// <summary>The container has its own lease row and owner, is dealt by the planner, and has cost telemetry and capacity.</summary>
        Leased = 1,
        /// <summary>Whoever owns the parent simulates what is inside (for a carried container: the carrier's authority).</summary>
        Inherited = 2,
    }

    /// <summary>Where a container's box is (<c>docs/container-tree.md</c> D1).</summary>
    public enum ContainerFrameMode : byte
    {
        /// <summary>Placed relative to its parent (or the scope, for a root) and never moves on its own.</summary>
        Fixed = 0,
        /// <summary>Driven by a carrier entity (<see cref="DynamicContainer"/>): moves with it.</summary>
        Entity = 1,
    }

    /// <summary>Where a container comes from (<c>docs/container-tree.md</c> D1).</summary>
    public enum ContainerSource : byte
    {
        /// <summary>Authored into a scene or baked into the world manifest.</summary>
        Baked = 0,
        /// <summary>Registered while the mesh runs (a lease row that carries a box).</summary>
        Runtime = 1,
        /// <summary>Part of a prefab: carried by an entity.</summary>
        Prefab = 2,
    }

    /// <summary>
    /// How the contents of a container with its own physics frame are bucketed for interest management
    /// (<c>docs/container-tree.md</c> D18).
    /// </summary>
    public enum FrameInterestMode : byte
    {
        /// <summary>
        /// Everything inside is published with the carrier, wherever the carrier is: a client sees the crew of a
        /// ship exactly when it sees the ship. Right for ships, vehicles and anything a client takes in at a glance.
        /// </summary>
        WithCarrier = 0,
        /// <summary>
        /// Everything inside is bucketed in regions of the frame itself, keyed from frame-local positions: a
        /// client inside (or in a frame nested inside) sees what is near it in this frame. Right for a planet,
        /// whose surface must not be one region, and whose rotation must not churn regions in system space.
        /// </summary>
        OwnRegions = 1,
    }

    /// <summary>
    /// A 64-bit-per-axis position. Container placements that are absolute (a root container 10,000 km out) are
    /// kept in this form end to end and narrowed to <c>float</c> only after the process's origin has been taken
    /// off, which is what makes a far placement exact (<c>docs/container-tree.md</c> D5).
    /// </summary>
    [Serializable]
    public struct Double3 : IEquatable<Double3>
    {
        public double X, Y, Z;

        public Double3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Double3 Zero = new Double3(0, 0, 0);

        /// <summary>Widen a float vector.</summary>
        public static Double3 From(Vector3 v) => new Double3(v.x, v.y, v.z);

        /// <summary>Narrow to a float vector. Take any origin off first.</summary>
        public Vector3 ToVector3() => new Vector3((float)X, (float)Y, (float)Z);

        public static Double3 operator +(Double3 a, Double3 b) => new Double3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Double3 operator -(Double3 a, Double3 b) => new Double3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Double3 operator +(Double3 a, Vector3 b) => new Double3(a.X + b.x, a.Y + b.y, a.Z + b.z);
        public static Double3 operator -(Double3 a, Vector3 b) => new Double3(a.X - b.x, a.Y - b.y, a.Z - b.z);

        public bool Equals(Double3 other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is Double3 d && Equals(d);
        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() * 397) ^ (Z.GetHashCode() * 7919);
        public static bool operator ==(Double3 a, Double3 b) => a.Equals(b);
        public static bool operator !=(Double3 a, Double3 b) => !a.Equals(b);
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2})", X, Y, Z);
    }

    /// <summary>
    /// Where a runtime container goes and what kind of container it is: its parent, its box, its authority and
    /// whether it gets its own physics frame. This is what a lease row carries (<see cref="LeaseInfo.Placement"/>)
    /// and what <see cref="NebulaWorker.RequestRuntimeContainer(ulong, ContainerPlacement, InstanceContainerInfo)"/>
    /// and <see cref="ContainerRegistry.RegisterRuntime(ulong, ContainerPlacement, InstanceContainerInfo)"/> take.
    /// <para>
    /// A <b>root</b> (no <see cref="ParentId"/>) is placed in its scope: <see cref="Center"/> is absolute and kept
    /// in double, so a root far from the origin is placed exactly. A <b>child</b> is placed in its parent's frame:
    /// <see cref="Center"/> is local to the parent's origin and the box is axis-aligned in the parent's frame, so
    /// it moves and turns with the parent. See <c>docs/container-tree.md</c>.
    /// </para>
    /// <para>A <c>Bounds</c> converts to a root placement, so every call that took an absolute box still compiles.</para>
    /// </summary>
    public struct ContainerPlacement
    {
        /// <summary>The parent container's id, or empty for a root of its scope.</summary>
        public string ParentId;
        /// <summary>Box centre: absolute for a root, parent-local for a child.</summary>
        public Double3 Center;
        /// <summary>Box size, in the parent's frame.</summary>
        public Vector3 Size;
        /// <summary>Who simulates what is inside (<see cref="ContainerAuthority"/>); <see cref="ContainerAuthority.Auto"/> is leased.</summary>
        public ContainerAuthority Authority;
        /// <summary>The container gets a physics frame of its own (<c>docs/container-tree.md</c> §3).</summary>
        public bool OwnPhysicsFrame;
        /// <summary>How a framed container's contents are bucketed for interest (<see cref="FrameInterestMode"/>).</summary>
        public FrameInterestMode FrameInterest;

        /// <summary>No parent: the box is placed in its scope.</summary>
        public bool IsRoot => string.IsNullOrEmpty(ParentId);

        /// <summary>Whether what is inside is simulated by the parent's owner rather than by a lease of its own.</summary>
        public bool IsInherited => Authority == ContainerAuthority.Inherited;

        /// <summary>A root box at an absolute centre.</summary>
        public static ContainerPlacement Root(Double3 center, Vector3 size, ContainerAuthority authority = ContainerAuthority.Auto) =>
            new ContainerPlacement { ParentId = "", Center = center, Size = size, Authority = authority };

        /// <summary>A root box from an absolute <c>Bounds</c>.</summary>
        public static ContainerPlacement Root(Bounds absolute) => Root(Double3.From(absolute.center), absolute.size);

        /// <summary>A child box, centred at <paramref name="localCenter"/> in <paramref name="parentId"/>'s frame.</summary>
        public static ContainerPlacement Child(string parentId, Vector3 localCenter, Vector3 size, ContainerAuthority authority = ContainerAuthority.Auto) =>
            new ContainerPlacement { ParentId = parentId ?? "", Center = Double3.From(localCenter), Size = size, Authority = authority };

        /// <summary>The same placement with its own physics frame.</summary>
        public ContainerPlacement WithPhysicsFrame(FrameInterestMode interest = FrameInterestMode.WithCarrier)
        {
            var p = this;
            p.OwnPhysicsFrame = true;
            p.FrameInterest = interest;
            return p;
        }

        public static implicit operator ContainerPlacement(Bounds absolute) => Root(absolute);

        /// <summary>The box with its centre narrowed to float, as given (absolute for a root, local for a child).</summary>
        public Bounds Bounds => new Bounds(Center.ToVector3(), Size);

        public override string ToString() =>
            $"{(IsRoot ? "root" : "in " + ParentId)} {Center} size {Size}{(Authority != ContainerAuthority.Auto ? " " + Authority : "")}{(OwnPhysicsFrame ? " framed" : "")}";
    }
}
