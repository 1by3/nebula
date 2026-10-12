using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Lookup for portals (<c>docs/portals.md</c> D1): the <see cref="Portal"/> components on this process, on a client what the
    /// network gateway has told it about the portals near it, and the link poses between scopes (<see cref="Map(ulong, ulong, PortalPose)"/>,
    /// <see cref="Turn"/>). Per-scope drawing slots are added by a later milestone of the same design.
    /// </summary>
    public static class Portals
    {
        private static readonly List<Portal> Registered = new List<Portal>();
        private static int _registryVersion;

        /// <summary>Every enabled portal component on this process.</summary>
        public static IReadOnlyList<Portal> All => Registered;

        internal static void Register(Portal portal)
        {
            if (!Registered.Contains(portal)) { Registered.Add(portal); _registryVersion++; }
        }

        internal static void Unregister(Portal portal)
        {
            if (Registered.Remove(portal)) _registryVersion++;
        }

        /// <summary>The enabled portal component with this derived id (<see cref="Portal.PortalId"/>), or null.</summary>
        public static Portal Find(ulong portalId)
        {
            for (int i = 0; i < Registered.Count; i++)
            {
                var portal = Registered[i];
                if (portal != null && !portal.IsIgnored && portal.PortalId == portalId) return portal;
            }
            return null;
        }

        /// <summary>The live link with this id (<see cref="PortalLink.Id"/>) of any enabled portal on this process, or null.</summary>
        public static PortalLink FindLink(ulong linkId)
        {
            for (int i = 0; i < Registered.Count; i++)
            {
                var portal = Registered[i];
                if (portal == null || portal.IsIgnored) continue;
                foreach (var link in portal.Links) if (link.Id == linkId) return link;
            }
            return null;
        }

        /// <summary>
        /// Whether a link that is not closed leads into this container (<c>docs/portals.md</c> D43): a preparing, open or closing link keeps
        /// its far scope from being judged idle, as an occupant does, so a door left open onto an empty room does not lose the room.
        /// </summary>
        internal static bool HoldsFarContainer(Container container)
        {
            for (int i = 0; i < Registered.Count; i++)
            {
                var portal = Registered[i];
                if (portal == null || portal.IsIgnored || portal.LinkCount == 0) continue;
                foreach (var link in portal.Links)
                    if (link.State != PortalState.Closed && link.FarEntry != null && link.FarEntry == container) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------ crossing

        // The portals a worker owns, indexed by the container their near face is fixed in and by the far scope of their links, rebuilt once
        // a worker tick. An entity is asked about the portals on its container chain, not about every portal in the process
        // (docs/portals.md D15).
        private static readonly Dictionary<Container, List<Portal>> ByContainer = new Dictionary<Container, List<Portal>>();
        private static readonly Dictionary<ulong, List<Portal>> ByFarScope = new Dictionary<ulong, List<Portal>>();
        private static readonly List<List<Portal>> Spare = new List<List<Portal>>();
        private static NebulaWorker _indexWorker;
        private static uint _indexTick = uint.MaxValue;
        private static int _indexVersion = -1;

        private static List<Portal> NewList()
        {
            if (Spare.Count == 0) return new List<Portal>(2);
            var list = Spare[Spare.Count - 1];
            Spare.RemoveAt(Spare.Count - 1);
            return list;
        }

        private static void ClearIndex()
        {
            foreach (var list in ByContainer.Values) { list.Clear(); Spare.Add(list); }
            foreach (var list in ByFarScope.Values) { list.Clear(); Spare.Add(list); }
            ByContainer.Clear();
            ByFarScope.Clear();
        }

        private static void EnsureIndex(NebulaWorker worker)
        {
            if (_indexWorker == worker && _indexTick == worker.CurrentTick && _indexVersion == _registryVersion) return;
            _indexWorker = worker;
            _indexTick = worker.CurrentTick;
            _indexVersion = _registryVersion;
            ClearIndex();
            for (int i = 0; i < Registered.Count; i++)
            {
                var portal = Registered[i];
                if (portal == null || portal.IsIgnored) continue;
                var near = portal.NearContainer;
                if (near == null || near.OwnerWorkerId != worker.WorkerId) continue;
                if (!ByContainer.TryGetValue(near, out var here)) ByContainer.Add(near, here = NewList());
                here.Add(portal);
                foreach (var link in portal.Links)
                {
                    // A closed link still owns the scope it led to: an occupant it closed under meets the aperture from inside.
                    if (link.FarScope == 0) continue;
                    if (!ByFarScope.TryGetValue(link.FarScope, out var there)) ByFarScope.Add(link.FarScope, there = NewList());
                    if (!there.Contains(portal)) there.Add(portal);
                }
            }
        }

        /// <summary>
        /// Test one entity this worker simulates against the portals that can matter to it, once a tick, after the entity's own simulation
        /// (<c>docs/portals.md</c> D14, D15). Every authoritative, spawned entity crosses on its own, player-owned or server-owned, except an
        /// authored scene entity, a pinned one (<c>docs/frame-bodies.md</c> D8) and one that is carried: it rides in the box of a carrier below
        /// the portal and goes with its carrier. A portal is asked when it stands in a container on the entity's chain or in a neighbour of the
        /// one it is in (a door beyond a chunk seam), or when one of its links leads into the scope the entity is in.
        /// </summary>
        internal static void Tick(NebulaWorker worker, NetworkIdentity entity)
        {
            if (Registered.Count == 0 || !entity.HasAuthority || !entity.IsSpawned || entity.IsSceneEntity || entity.ContainerPinned) return;
            EnsureIndex(worker);
            var innermost = entity.Container;
            if (innermost == null) return;
            if (ByContainer.Count > 0)
            {
                bool carried = false;
                int bound = ContainerRegistry.ChainBound;
                int hops = 0;
                for (var c = innermost; c != null && hops <= bound; c = c.Parent, hops++)
                {
                    if (ByContainer.TryGetValue(c, out var here)) Ask(worker, entity, here);
                    // Whatever lies above a carrier's box is the carrier's to cross, not its passengers'.
                    if (c.IsDynamic) { carried = true; break; }
                }
                if (!carried)
                {
                    var neighbors = innermost.Neighbors;
                    for (int i = 0; i < neighbors.Count; i++)
                    {
                        var n = neighbors[i];
                        if (n != null && ByContainer.TryGetValue(n, out var there)) Ask(worker, entity, there);
                    }
                }
            }
            ulong scope = innermost.InstanceId;
            if (scope != 0 && ByFarScope.Count > 0 && ByFarScope.TryGetValue(scope, out var leading) && !RidesAnywhere(entity)) Ask(worker, entity, leading);
        }

        /// <summary>How many times a portal was asked about an entity on this process: what the index keeps down (tests).</summary>
        internal static int Asks;

        private static void Ask(NebulaWorker worker, NetworkIdentity entity, List<Portal> portals)
        {
            Asks += portals.Count;
            for (int i = 0; i < portals.Count; i++) portals[i].Cross(worker, entity);
        }

        /// <summary>Whether the entity rides in the box of a carrier anywhere on its container chain.</summary>
        internal static bool RidesAnywhere(NetworkIdentity entity)
        {
            int bound = ContainerRegistry.ChainBound;
            int hops = 0;
            for (var c = entity.Container; c != null && hops <= bound; c = c.Parent, hops++) if (c.IsDynamic) return true;
            return false;
        }

        // ------------------------------------------------------------------------------------------ link poses

        /// <summary>
        /// A pose of scope <paramref name="from"/> in scope <paramref name="to"/>, carried through the link between them (<c>docs/portals.md</c>
        /// D39): the position about the faces and in double, the rotation turned. Scopes that no portal and no instance pose relates, and a
        /// scope with itself, return the pose unchanged. For an instance a template portal built this is its pose against its host
        /// (<see cref="InstancePoses"/>), which every process learns from lease rows; for a portal with a fixed far side it is the portal's two
        /// faces, known to a worker for the links it holds and to a client from the portal records it was sent. Where several portals join
        /// the same two scopes, the one whose face is nearest the pose is used.
        /// </summary>
        public static PortalPose Map(ulong from, ulong to, PortalPose pose)
        {
            if (!TryMap(from, to, pose.Position, out var map)) return pose;
            return new PortalPose(map.Point(pose.Position), map.Rotation(pose.Rotation));
        }

        /// <summary><see cref="Map(ulong, ulong, PortalPose)"/> for a position alone.</summary>
        public static Double3 Map(ulong from, ulong to, Double3 position) =>
            TryMap(from, to, position, out var map) ? map.Point(position) : position;

        /// <summary>
        /// What turns a rotation or direction of scope <paramref name="from"/>'s axes into scope <paramref name="to"/>'s, for an entity
        /// crossing between them through a portal: the identity unless the link between them is turned. A game keeping its own heading or
        /// velocity bookkeeping (a pawn's yaw) applies it in a <see cref="Portal.Crossed"/> handler. It replaces <see cref="InstancePoses.Turn"/>
        /// for anything that crosses a portal, and answers for a portal with a fixed far side too.
        /// </summary>
        public static Quaternion Turn(ulong from, ulong to) =>
            TryMap(from, to, Double3.Zero, out var map) ? map.Turn : Quaternion.identity;

        /// <summary>
        /// The map between two scopes: a link a worker holds, a portal a client was told of, or the pose of the instance one of them is.
        /// <paramref name="near"/> (an absolute point of <paramref name="from"/>) chooses the portal whose face is nearest when several join
        /// the same scopes; the zero point asks for any. False when nothing relates the scopes.
        /// </summary>
        internal static bool TryMap(ulong from, ulong to, Double3 near, out PortalMap map)
        {
            map = PortalMap.Identity(from);
            if (from == to) return true;
            double best = double.MaxValue;
            bool found = false;
            // A worker's links.
            for (int i = 0; i < Registered.Count; i++)
            {
                var portal = Registered[i];
                if (portal == null || portal.IsIgnored || portal.LinkCount == 0) continue;
                ulong nearScope = portal.NearScope;
                foreach (var link in portal.Links)
                {
                    if (link.FarScope == 0) continue;
                    bool forward = nearScope == from && link.FarScope == to;
                    bool backward = nearScope == to && link.FarScope == from;
                    if (!forward && !backward) continue;
                    var candidate = forward ? link.ToFarMap : link.ToNearMap;
                    double d = Distance(candidate.FromOrigin, near);
                    if (d < best) { best = d; map = candidate; found = true; }
                }
            }
            if (found) return true;
            // A client's records: a portal with a fixed far side. A template's pose is the instance's (below).
            foreach (var kv in KnownLinkRecords)
            {
                if (!KnownPortals.TryGetValue(kv.Value.PortalId, out var record) || record.FarKind != PortalFarKind.Fixed) continue;
                ulong farScope = kv.Value.FarScope != 0 ? kv.Value.FarScope : record.FarScope;
                bool forward = record.NearScope == from && farScope == to;
                bool backward = record.NearScope == to && farScope == from;
                if (!forward && !backward) continue;
                var nearToFar = new PortalMap(record.NearScope, farScope, record.NearOrigin, record.FarOrigin,
                    record.FarRotation * Quaternion.Inverse(record.NearRotation), record.PortalId);
                var candidate = forward ? nearToFar : nearToFar.Inverse;
                double d = Distance(candidate.FromOrigin, near);
                if (d < best) { best = d; map = candidate; found = true; }
            }
            if (found) return true;
            // An instance a template portal built: its pose against its host (a point of the instance is origin + R (p - origin) there).
            bool fromTurned = InstancePoses.TryGet(from, out var fromOrigin, out var fromRotation);
            bool toTurned = InstancePoses.TryGet(to, out var toOrigin, out var toRotation);
            if (!fromTurned && !toTurned) return false;
            if (fromTurned && !toTurned) { map = new PortalMap(from, to, fromOrigin, fromOrigin, fromRotation); return true; }
            if (!fromTurned) { map = new PortalMap(from, to, toOrigin, toOrigin, Quaternion.Inverse(toRotation)); return true; }
            // Two instances of one host: out of the first and into the second.
            map = new PortalMap(from, to, fromOrigin, toOrigin, Quaternion.Inverse(toRotation) * fromRotation);
            return true;
        }

        /// <summary>
        /// How far, in meters, from a face a copy that changed scope may have been drawn or predicted and still have gone through that portal: a
        /// replica a render delay behind, or a predicted pawn a round trip ahead, is not standing at the plane when the change reaches it.
        /// </summary>
        internal const double CarryRange = 48.0;

        /// <summary>
        /// A copy this process does not simulate is changing container across scopes: the map of the link it went through, when a portal or an
        /// instance's pose relates the scopes and the copy was near enough to its face to have used it; its position, absolute in <paramref name="from"/>'s scope.
        /// </summary>
        internal static bool TryCarryReplica(NetworkIdentity entity, Container from, Container to, out PortalMap map, out Double3 absolute)
        {
            map = default;
            absolute = default;
            if (Registered.Count == 0 && KnownLinkRecords.Count == 0 && InstancePoses.Count == 0) return false;
            absolute = ContainerRegistry.ToAbsolutePrecise(entity.ScopePositionPrecise, from.InstanceId);
            return TryMap(from.InstanceId, to.InstanceId, absolute, out map) && Distance(map.FromOrigin, absolute) <= CarryRange;
        }

        /// <summary>
        /// As <see cref="TryCarryReplica"/>, for a buffer of poses (an interpolation buffer) whose newest sample is in <paramref name="latest"/>
        /// at <paramref name="latestWorld"/>, about to take a sample in <paramref name="to"/>.
        /// </summary>
        internal static bool TryCarryBuffer(Container latest, Vector3 latestWorld, Container to, out PortalMap map)
        {
            map = default;
            ulong from = latest != null ? latest.InstanceId : 0UL;
            ulong scope = to != null ? to.InstanceId : 0UL;
            if (from == scope || (Registered.Count == 0 && KnownLinkRecords.Count == 0 && InstancePoses.Count == 0)) return false;
            var absolute = ContainerRegistry.ToAbsolutePrecise(latestWorld, from);
            return TryMap(from, scope, absolute, out map) && Distance(map.FromOrigin, absolute) <= CarryRange;
        }

        /// <summary>
        /// A pose held in <paramref name="from"/>'s local space (the world when null), in the scope the map leaves, as a pose in
        /// <paramref name="to"/>'s local space, in the scope it enters; the velocity is in scope axes and is turned. Float throughout, as the
        /// buffers that hold such poses are: they are samples near the face, not positions to be kept to the millimetre.
        /// </summary>
        internal static void CarryLocal(in PortalMap map, Container from, Vector3 position, Quaternion rotation, Vector3 velocity,
            Container to, out Vector3 carriedPosition, out Quaternion carriedRotation, out Vector3 carriedVelocity)
        {
            Vector3 world = position;
            Quaternion worldRotation = rotation;
            if (from != null)
            {
                world = from.ToWorld(position);
                worldRotation = from.Rotation * rotation;
                var space = from.Space;
                if (PhysicsFrames.InSimulationPose(space))
                {
                    world = PhysicsFrames.Convert(world, space, null);
                    worldRotation = PhysicsFrames.Convert(worldRotation, space, null);
                }
            }
            var absolute = ContainerRegistry.ToAbsolutePrecise(world, map.FromScope);
            var frame = ContainerRegistry.ToFrame(map.Point(absolute), map.ToScope);
            var frameRotation = map.Rotation(worldRotation);
            carriedVelocity = map.Direction(velocity);
            if (to == null)
            {
                carriedPosition = frame;
                carriedRotation = frameRotation;
                return;
            }
            var toSpace = to.Space;
            if (PhysicsFrames.InSimulationPose(toSpace))
            {
                frame = PhysicsFrames.Convert(frame, null, toSpace);
                frameRotation = PhysicsFrames.Convert(frameRotation, null, toSpace);
            }
            carriedPosition = to.ToLocal(frame);
            carriedRotation = to.InverseRotation * frameRotation;
        }

        private static double Distance(Double3 a, Double3 b)
        {
            double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
            return Math.Sqrt(x * x + y * y + z * z);
        }

        /// <summary>
        /// Raised on this process when an entity crossed between two scopes through a portal: on a worker for every committed member, on a
        /// client when the entity's container change arrived and was carried through the link. <see cref="Portal.Crossed"/> says the same for
        /// one portal, where its component is known to the process (a client may not have loaded the content a portal stands in).
        /// Arguments: the entity, the portal's derived id (0 when only an instance's pose related the scopes), the scope it left and the scope it entered.
        /// </summary>
        public static event Action<NetworkIdentity, ulong, ulong, ulong> Crossed;

        internal static void RaiseCrossed(NetworkIdentity entity, ulong portalId, ulong from, ulong to)
        {
            var portal = portalId != 0 ? Find(portalId) : null;
            if (portal != null) portal.RaiseCrossed(entity, from, to);
            var handler = Crossed;
            if (handler == null) return;
            try { handler(entity, portalId, from, to); }
            catch (Exception e) { NebulaLog.Error($"a Portals.Crossed handler threw: {e}"); }
        }

        // ------------------------------------------------------------------------------------------ on a client

        private static readonly Dictionary<ulong, PortalRecord> KnownPortals = new Dictionary<ulong, PortalRecord>();
        private static readonly Dictionary<ulong, PortalLinkRecord> KnownLinkRecords = new Dictionary<ulong, PortalLinkRecord>();

        /// <summary>
        /// On a client: the portals the network gateway has told it about, by derived id. A portal is sent behind the container row of
        /// either of its faces and withdrawn with the row (<c>docs/portals.md</c> D45).
        /// </summary>
        public static IReadOnlyDictionary<ulong, PortalRecord> Known => KnownPortals;

        /// <summary>
        /// On a client: the state of each link the network gateway has told it about, by link id. A link is sent to the clients that
        /// hold the container row of either face, with whether this client is a member and whether it may look through.
        /// </summary>
        public static IReadOnlyDictionary<ulong, PortalLinkRecord> KnownLinks => KnownLinkRecords;

        /// <summary>Raised on a client when a portal record or a link record arrived or was withdrawn.</summary>
        public static event Action KnownChanged;

        internal static void ApplyRecords(PortalsUpdate update)
        {
            foreach (var record in update.Upserts) KnownPortals[record.PortalId] = record;
            foreach (ulong id in update.Removes)
            {
                KnownPortals.Remove(id);
                // A portal's links go with it.
                List<ulong> gone = null;
                foreach (var kv in KnownLinkRecords) if (kv.Value.PortalId == id) (gone ??= new List<ulong>()).Add(kv.Key);
                if (gone != null) foreach (ulong linkId in gone) KnownLinkRecords.Remove(linkId);
            }
            RaiseKnownChanged();
        }

        internal static void ApplyLinks(PortalLinksUpdate update)
        {
            foreach (var record in update.Upserts) KnownLinkRecords[record.LinkId] = record;
            foreach (ulong id in update.Removes) KnownLinkRecords.Remove(id);
            RaiseKnownChanged();
        }

        internal static void ResetForNewSession()
        {
            Registered.Clear();
            _registryVersion++;
            ClearIndex();
            _indexWorker = null;
            _indexTick = uint.MaxValue;
            ResetClient();
        }

        internal static void ResetClient()
        {
            if (KnownPortals.Count == 0 && KnownLinkRecords.Count == 0) return;
            KnownPortals.Clear();
            KnownLinkRecords.Clear();
            RaiseKnownChanged();
        }

        private static void RaiseKnownChanged()
        {
            var handler = KnownChanged;
            if (handler == null) return;
            try { handler(); }
            catch (Exception e) { NebulaLog.Error($"a Portals.KnownChanged handler threw: {e}"); }
        }
    }
}
