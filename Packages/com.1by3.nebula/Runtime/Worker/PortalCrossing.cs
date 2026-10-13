using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Crossing, on the worker that simulates the entity and owns the portal (<c>docs/portals.md</c> D14 to D18). A link that is open has
    /// its far side ready already, so a crossing prepares and loads nothing: the entity is committed on the tick its root passes the
    /// face, or, for a body that moves further than <see cref="CrossDepth"/> in a step, on the tick before the step that would carry it
    /// through. Any entity this worker simulates crosses on its own (<see cref="Portals.Tick"/> decides which portals are asked); one the
    /// link does not let through is stopped at the face and <see cref="Refused"/> is raised.
    /// </summary>
    public sealed partial class Portal
    {
        private sealed class Crossing
        {
            public NetworkIdentity Entity;
            /// <summary>Where the entity stood the last time this portal looked, absolute in the scope it was in then.</summary>
            public Double3 Last;
            public ulong LastScope;
            public uint LastTick;
            public bool HasLast;
            /// <summary>What crosses with it (<see cref="ResolveCompanions"/>).</summary>
            public readonly List<NetworkIdentity> Companions = new List<NetworkIdentity>();
        }

        /// <summary>A crossing nobody has asked about for this many ticks is dropped: the entity moved away from the face.</summary>
        private const uint StaleCrossingTicks = 120;

        private readonly Dictionary<ulong, Crossing> _crossings = new Dictionary<ulong, Crossing>();
        /// <summary>Each companion's leader, by net id.</summary>
        private readonly Dictionary<ulong, ulong> _leaderOf = new Dictionary<ulong, ulong>();
        private static readonly List<NetworkIdentity> Resolved = new List<NetworkIdentity>();
        private static readonly List<NetworkIdentity> Group = new List<NetworkIdentity>();
        private static readonly List<ulong> StaleScratch = new List<ulong>();

        // The near face is read once a tick per worker for the crossing test, not once per entity.
        private PortalFace _faceCache;
        private uint _faceCacheTick = uint.MaxValue;
        private NebulaWorker _faceCacheWorker;

        internal PortalFace NearFaceOf(NebulaWorker worker)
        {
            if (_faceCacheWorker != worker || _faceCacheTick != worker.CurrentTick)
            {
                _faceCache = NearFace;
                _faceCacheTick = worker.CurrentTick;
                _faceCacheWorker = worker;
            }
            return _faceCache;
        }

        private static Double3 AbsoluteOf(NetworkIdentity entity) =>
            ContainerRegistry.ToAbsolutePrecise(entity.ScopePositionPrecise, entity.InstanceId);

        /// <summary>An entity's rotation in its scope's own space (out of a physics frame it simulates in).</summary>
        private static Quaternion ScopeRotationOf(NetworkIdentity entity)
        {
            var space = entity.Space;
            return PhysicsFrames.InSimulationPose(space) ? PhysicsFrames.Convert(entity.transform.rotation, space, null) : entity.transform.rotation;
        }

        /// <summary>An entity's velocity in its scope's own axes: its dynamic body's when it has one, else the velocity it reports.</summary>
        private static Vector3 ScopeVelocityOf(NetworkIdentity entity)
        {
            var space = entity.Space;
            var body = entity.GetComponent<Rigidbody>();
            var velocity = body != null && !body.isKinematic ? body.linearVelocity : entity.Motion.Velocity;
            return PhysicsFrames.InSimulationPose(space) ? PhysicsFrames.ConvertVelocity(velocity, entity.transform.position, space, null) : velocity;
        }

        private PortalLink LinkByFarScope(ulong scope)
        {
            foreach (var link in _links.Values)
                if (link.FarScope == scope && link.FarScope != 0 && link.State != PortalState.Closed) return link;
            // A link that closed under an occupant still owns the scope it leads to: the occupant meets the aperture from inside.
            foreach (var link in _links.Values) if (link.FarScope == scope && link.FarScope != 0) return link;
            return null;
        }

        private bool AnyLinkOpen()
        {
            foreach (var link in _links.Values) if (link.State == PortalState.Open) return true;
            return false;
        }

        private string PartyOfEntity(NetworkIdentity entity)
        {
            if (PartyOf != null)
            {
                try { return PartyOf(entity); }
                catch (Exception e) { NebulaLog.Error($"Portal '{name}': a PartyOf handler threw: {e}"); return null; }
            }
            return entity.OwnerClientId != 0 ? entity.OwnerIdentity : null;
        }

        /// <summary>
        /// The link an entity at the near face would go through (<c>docs/portals.md</c> D12): its party's, when it has one; otherwise the face's
        /// one open link when there is exactly one; otherwise none, and the reason.
        /// </summary>
        private PortalLink ResolveLink(NetworkIdentity entity, out PortalRefusal refusal)
        {
            refusal = PortalRefusal.None;
            string party = PartyOfEntity(entity);
            if (party != null && _links.TryGetValue(party, out var own)) return own;
            PortalLink only = null;
            int open = 0;
            foreach (var link in _links.Values) if (link.State == PortalState.Open) { only = link; open++; }
            if (open == 1) return only;
            refusal = open > 1 ? PortalRefusal.AmbiguousParty : PortalRefusal.LinkNotOpen;
            return null;
        }

        /// <summary>
        /// Whether a body at this portal's near face would be let through <paramref name="link"/> (<c>docs/portals.md</c> D12, D28): its party's link is that
        /// one, the link is open, the portal lets anything through and <see cref="CanEnter"/> (or the default) admits it. The reason when it would not.
        /// </summary>
        internal bool AdmitsThrough(NetworkIdentity entity, PortalLink link, out PortalRefusal why)
        {
            why = PortalRefusal.None;
            if (ObservationOnly) { why = PortalRefusal.NotAdmitted; return false; }
            var resolved = ResolveLink(entity, out why);
            if (resolved == null) return false;
            if (resolved != link) { why = PortalRefusal.NotAdmitted; return false; }
            if (link.State != PortalState.Open) { why = PortalRefusal.LinkNotOpen; return false; }
            if (!Admits(entity, link)) { why = PortalRefusal.NotAdmitted; return false; }
            return true;
        }

        /// <summary>
        /// Whether <paramref name="entity"/> may cross <paramref name="link"/> in the direction it is going: entering is <see cref="CanEnter"/>'s (or the default's); leaving is
        /// unchecked unless the game set <see cref="CanLeave"/>, so an occupant can always leave a portal that never asks.
        /// </summary>
        private bool Allows(NetworkIdentity entity, PortalLink link, bool leaving)
        {
            if (!leaving) return Admits(entity, link);
            var hook = CanLeave;
            if (hook == null) return true;
            try { return hook(entity, link); }
            catch (Exception e) { NebulaLog.Error($"Portal '{name}': a CanLeave handler threw: {e}"); return false; }
        }

        private bool Admits(NetworkIdentity entity, PortalLink link)
        {
            if (CanEnter != null)
            {
                try { return CanEnter(entity, link); }
                catch (Exception e) { NebulaLog.Error($"Portal '{name}': a CanEnter handler threw: {e}"); return false; }
            }
            return entity.OwnerClientId == 0 || (link.IsMember(entity.OwnerClientId) && link.IsWarm(entity.OwnerClientId));
        }

        private void PruneCrossings(uint tick)
        {
            StaleScratch.Clear();
            foreach (var kv in _crossings)
            {
                var crossing = kv.Value;
                bool gone = crossing.Entity == null || !crossing.Entity.IsSpawned || !crossing.Entity.HasAuthority;
                // Nobody has looked at it for a while: it moved away from the face (a companion is looked at by its leader's crossing only).
                bool stale = !gone && crossing.Companions.Count == 0 && tick - crossing.LastTick > StaleCrossingTicks;
                if (gone || stale) StaleScratch.Add(kv.Key);
            }
            for (int i = 0; i < StaleScratch.Count; i++)
            {
                if (_crossings.TryGetValue(StaleScratch[i], out var dropped)) ReleaseCompanions(dropped);
                _crossings.Remove(StaleScratch[i]);
            }
            StaleScratch.Clear();
        }

        /// <summary>
        /// Test one entity against this portal. Called by <see cref="Portals.Tick"/> for the entities this worker simulates that can matter
        /// to it: those on the face's container chain or in a neighbour of it, and those in a scope one of its links leads into.
        /// </summary>
        internal void Cross(NebulaWorker worker, NetworkIdentity entity)
        {
            if (_leaderOf.Count > 0 && IsLedCompanion(entity)) return;
            var near = NearContainer;
            if (near == null || near.OwnerWorkerId != worker.WorkerId) return;
            ulong scope = entity.InstanceId;
            var nearFace = NearFaceOf(worker);
            PortalLink link = null;
            PortalFace face;
            bool leaving;
            if (scope == nearFace.Scope)
            {
                leaving = false;
                face = nearFace;
            }
            else
            {
                link = LinkByFarScope(scope);
                if (link == null) { Forget(entity); return; }
                leaving = true;
                face = link.FarFace;
            }
            var absolute = AbsoluteOf(entity);
            var local = face.ToLocal(absolute);
            double step = NetworkTime.TickInterval * Math.Max(1, entity.UpdateInterval);
            // A body is tracked while it can reach the face within a step as well as while it is near it. (A rigidbody's reported velocity
            // is its own, mirrored every tick, so the far majority of entities are rejected without looking for a body.)
            double reach = ApproachDistance + entity.Motion.Velocity.magnitude * step;
            bool inZone = Math.Abs(local.X) <= Aperture.HalfWidth + reach && Math.Abs(local.Y) <= Aperture.HalfHeight + reach && Math.Abs(local.Z) <= reach;
            if (!inZone) { Forget(entity); return; }
            var velocity = ScopeVelocityOf(entity);
            if (!_crossings.TryGetValue(entity.NetId, out var crossing)) _crossings.Add(entity.NetId, crossing = new Crossing());
            crossing.Entity = entity;
            uint tick = worker.CurrentTick;
            if (crossing.HasLast && (crossing.LastScope != scope || tick - crossing.LastTick > (uint)Math.Max(1, entity.UpdateInterval) + 1u)) crossing.HasLast = false;

            if (ResolveCompanions != null && (leaving ? link.State == PortalState.Open : AnyLinkOpen())) PrepareCompanions(entity, crossing);
            else if (crossing.Companions.Count > 0) ReleaseCompanions(crossing);

            // The swept segment, last look to this one, against the opening (D15); then, for a body that moves further than the depth in a
            // step, the segment of the step to come, so it is committed before the step that would carry it through.
            Double3 hit = default;
            bool passed = crossing.HasLast && Passes(face.ToLocal(crossing.Last), local, leaving, out hit);
            bool predicted = false;
            if (!passed)
            {
                var along = PhysicsFrames.Rotate(Quaternion.Inverse(face.Rotation), Double3.From(velocity));
                var ahead = new Double3(along.X * step, along.Y * step, along.Z * step);
                double length = Math.Sqrt(ahead.X * ahead.X + ahead.Y * ahead.Y + ahead.Z * ahead.Z);
                predicted = length > CrossDepth && Passes(local, local + ahead, leaving, out hit);
            }
            if (!passed && !predicted)
            {
                Remember(crossing, absolute, scope, tick);
                // A body that meets the solid aperture collider never reaches the plane; the refusal is raised when it touches (docs/portals.md D17, D28).
                RefuseOnContact(worker, entity, leaving ? link : null, leaving);
                return;
            }

            var refusal = PortalRefusal.None;
            if (!leaving) link = ResolveLink(entity, out refusal);
            if (!TryCross(worker, entity, crossing, link, leaving, absolute, refusal, out var why))
            {
                Stop(entity, face, hit, leaving);
                NebulaDiagnostics.PortalRefusals++;
                RaiseRefused(entity, link, why);
            }
            Remember(crossing, AbsoluteOf(entity), entity.InstanceId, tick);
        }

        private static void Remember(Crossing crossing, Double3 absolute, ulong scope, uint tick)
        {
            crossing.Last = absolute;
            crossing.LastScope = scope;
            crossing.LastTick = tick;
            crossing.HasLast = true;
        }

        private void Forget(NetworkIdentity entity)
        {
            if (_crossings.Count == 0 || !_crossings.TryGetValue(entity.NetId, out var crossing)) return;
            ReleaseCompanions(crossing);
            _crossings.Remove(entity.NetId);
        }

        /// <summary>
        /// Whether the segment from <paramref name="last"/> to <paramref name="now"/> (in the face's axes) meets the cross-section and ends at
        /// least <see cref="CrossDepth"/> beyond the plane (entering), or at least <see cref="CrossDepth"/> short of it (leaving): the hysteresis
        /// that keeps an entity at the face from crossing and returning on every tick (<c>docs/portals.md</c> D15). <paramref name="hit"/> is
        /// where the segment meets that plane, in the face's axes.
        /// </summary>
        private bool Passes(Double3 last, Double3 now, bool leaving, out Double3 hit)
        {
            hit = default;
            double plane = leaving ? -CrossDepth : CrossDepth;
            bool through = leaving ? last.Z > plane && now.Z <= plane : last.Z < plane && now.Z >= plane;
            if (!through) return false;
            double t = (plane - last.Z) / (now.Z - last.Z);
            double x = last.X + t * (now.X - last.X);
            double y = last.Y + t * (now.Y - last.Y);
            hit = new Double3(x, y, plane);
            return Aperture.Contains(x, y);
        }

        /// <summary>
        /// The far container a crossing commits into (<c>docs/portals.md</c> D16): the deepest container of the far scope that holds the point the entity is
        /// mapped to, when it is one of the link's rows (every member's client is warm for each of them and the lease of each was checked); otherwise the
        /// nearest enclosing row, and the entry when nothing else is. A container the link did not warm is never entered, because its content would be built
        /// on the clients at the commit.
        /// </summary>
        private static Container FarDestination(PortalLink link, in PortalMap map, Double3 absolute, NetworkIdentity entity)
        {
            if (link.Rows.Count < 2) return link.FarEntry;
            var found = FindDestination(ContainerRegistry.ToFrame(map.Point(absolute), map.ToScope), map.ToScope, entity);
            int hops = 0;
            for (var c = found; c != null && hops <= ContainerRegistry.ChainBound; c = c.Parent, hops++)
                if (link.RowOf(c) != null) return c;
            return link.FarEntry;
        }

        private bool TryCross(NebulaWorker worker, NetworkIdentity entity, Crossing crossing, PortalLink link, bool leaving, Double3 absolute,
            PortalRefusal refusal, out PortalRefusal why)
        {
            why = PortalRefusal.None;
            if (ObservationOnly) { why = PortalRefusal.NotAdmitted; return false; }
            if (link == null) { why = refusal == PortalRefusal.None ? PortalRefusal.LinkNotOpen : refusal; return false; }
            if (link.State != PortalState.Open) { why = PortalRefusal.LinkNotOpen; return false; }
            if (!Allows(entity, link, leaving)) { why = PortalRefusal.NotAdmitted; return false; }

            var map = leaving ? link.ToNearMap : link.ToFarMap;
            Container destination;
            if (leaving)
            {
                // A container of the near scope that holds the point, never the nearest box of some other scope; none while that ground is not
                // leased here yet.
                destination = FindDestination(ContainerRegistry.ToFrame(map.Point(absolute), map.ToScope), map.ToScope, entity);
            }
            else destination = FarDestination(link, in map, absolute, entity);
            if (destination == null || !LeaseState.IsOwning(destination.LeaseState)) { why = PortalRefusal.NoDestination; return false; }
            if (!leaving)
            {
                var row = link.RowOf(destination);
                if (row == null || destination.LeaseEpoch != row.ReadyEpoch) { why = PortalRefusal.NoDestination; return false; }
            }

            // The leader with everything aboard it, and the companions that can cross with it, each with theirs (D14). A carrier whose riders this
            // worker does not simulate is refused whole: moving it would leave them in the scope it left (D35).
            if (!AboardIsWhole(worker, entity)) { why = PortalRefusal.SplitAboard; NebulaDiagnostics.PortalSplitRefusals++; return false; }
            Group.Clear();
            Group.Add(entity);
            if (!CollectAboard(worker, entity, link, leaving)) { Group.Clear(); why = PortalRefusal.NotAdmitted; return false; }
            foreach (var companion in crossing.Companions)
            {
                var problem = CompanionProblem(companion, entity);
                if (problem == PortalRefusal.None && !AboardIsWhole(worker, companion)) problem = PortalRefusal.SplitAboard;
                if (problem == PortalRefusal.None && !Allows(companion, link, leaving)) problem = PortalRefusal.NotAdmitted;
                if (problem == PortalRefusal.None)
                {
                    int mark = Group.Count;
                    Group.Add(companion);
                    if (!CollectAboard(worker, companion, link, leaving)) { Group.RemoveRange(mark, Group.Count - mark); problem = PortalRefusal.NotAdmitted; }
                }
                if (problem != PortalRefusal.None) ReportLeftBehind(entity, companion, problem);
            }
            var members = new List<NetworkIdentity>(Group);
            Group.Clear();
            if (!worker.CommitThrough(members, destination, in map))
            {
                why = PortalRefusal.NoDestination;
                return false;
            }
            ReleaseCompanions(crossing);
            NebulaDiagnostics.PortalCrossings++;
            if (!string.IsNullOrEmpty(destination.OwnerWorkerId) && destination.OwnerWorkerId != worker.WorkerId) NebulaDiagnostics.CrossWorkerPortalCrossings++;
            foreach (var member in members) Portals.RaiseCrossed(member, PortalId, map.FromScope, map.ToScope);
            return true;
        }

        private static readonly List<NetworkIdentity> AboardScratch = new List<NetworkIdentity>();

        /// <summary>
        /// Whether everything riding in <paramref name="carrier"/>'s box, at any depth, is simulated by this worker (<c>docs/portals.md</c> D35). A rider this
        /// process holds only as a ghost, or a box leased to another worker, belongs to a worker that cannot be told to follow within the tick, so the carrier
        /// stays where it is. A carrier with nothing aboard is whole.
        /// </summary>
        private static bool AboardIsWhole(NebulaWorker worker, NetworkIdentity carrier)
        {
            var box = carrier.Carried;
            if (box == null || !box.IsDynamic) return true;
            if (box.IsPinned && box.OwnerWorkerId != worker.WorkerId) return false;
            AboardScratch.Clear();
            box.CollectContents(AboardScratch, false);
            bool whole = true;
            for (int i = 0; i < AboardScratch.Count && whole; i++)
            {
                var rider = AboardScratch[i];
                if (rider != null && rider != carrier && !rider.HasAuthority) whole = false;
            }
            AboardScratch.Clear();
            return whole;
        }

        /// <summary>
        /// Add to <see cref="Group"/> everything this worker simulates that rides in <paramref name="carrier"/>'s box, at any depth (a cargo
        /// bay, the crew of a ship parked in a hangar): it crosses with its carrier and keeps its seat (<c>docs/scope-activation.md</c> D19, D21).
        /// False when a passenger is not allowed through <paramref name="link"/> in this direction (<see cref="Allows"/>), which refuses the carrier: nobody is carried into a
        /// scope their client has not been readied for.
        /// </summary>
        private bool CollectAboard(NebulaWorker worker, NetworkIdentity carrier, PortalLink link, bool leaving)
        {
            if (carrier.Carried == null || !carrier.Carried.IsDynamic) return true;
            var entities = worker.AuthoritativeEntities;
            for (int i = 0; i < entities.Count; i++)
            {
                var rider = entities[i];
                if (rider == null || rider == carrier || !rider.IsSpawned || !rider.HasAuthority || rider.IsSceneEntity || Group.Contains(rider)) continue;
                if (rider.Container == null || !rider.Container.IsCarriedBy(carrier)) continue;
                if (!Allows(rider, link, leaving)) return false;
                Group.Add(rider);
            }
            return true;
        }

        /// <summary>
        /// Stop an entity the link did not let through (<c>docs/portals.md</c> D17): its root goes back <see cref="CrossDepth"/> short of the
        /// plane, where its path met it, and the part of its velocity that pointed through is removed. A physical body also meets the
        /// throat's aperture collider (<c>docs/portals.md</c> D28); this stop is for every mover, a body included.
        /// </summary>
        private static void Stop(NetworkIdentity entity, PortalFace face, Double3 hit, bool leaving)
        {
            double z = leaving ? CrossDepth : -CrossDepth;
            var absolute = face.ToAbsolute(new Double3(hit.X, hit.Y, z));
            var origin = ContainerRegistry.ToAbsolutePrecise(Double3.Zero, face.Scope);
            entity.SetFramePose(null, absolute - origin, ScopeRotationOf(entity));
            var through = PhysicsFrames.DirectionFromScope(leaving ? -face.Forward : face.Forward, entity.Space);
            entity.Motion.Velocity = WithoutAlong(entity.Motion.Velocity, through);
            var body = entity.GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic) body.linearVelocity = WithoutAlong(body.linearVelocity, through);
        }

        private static Vector3 WithoutAlong(Vector3 velocity, Vector3 direction)
        {
            float along = Vector3.Dot(velocity, direction);
            return along > 0f ? velocity - along * direction : velocity;
        }

        private void RaiseRefused(NetworkIdentity entity, PortalLink link, PortalRefusal why)
        {
            var handler = Refused;
            if (handler == null) return;
            try { handler(entity, link, why); }
            catch (Exception e) { NebulaLog.Error($"Portal '{name}': a Refused handler threw: {e}"); }
        }

        internal void RaiseCrossed(NetworkIdentity entity, ulong from, ulong to)
        {
            var handler = Crossed;
            if (handler == null) return;
            try { handler(entity, this, from, to); }
            catch (Exception e) { NebulaLog.Error($"Portal '{name}': a Crossed handler threw: {e}"); }
        }

        private void ReportLeftBehind(NetworkIdentity leader, NetworkIdentity companion, PortalRefusal why)
        {
            var handler = CompanionLeftBehind;
            if (handler == null) return;
            try { handler(leader, companion, why); }
            catch (Exception e) { NebulaLog.Error($"Portal '{name}': a CompanionLeftBehind handler threw: {e}"); }
        }

        /// <summary>
        /// The deepest container of <paramref name="scope"/> that holds <paramref name="scopePoint"/> (a point of the scope's own space),
        /// descending into physics frames: a portal built in a chunk carried by a framed container (a planet, <c>docs/container-tree.md</c>
        /// D22) is left into that chunk, not the carrier's own box. Null while nothing holds the point here (that ground is not leased yet).
        /// </summary>
        internal static Container FindDestination(Vector3 scopePoint, ulong scope, NetworkIdentity entity)
        {
            var found = ContainerRegistry.FindInSpace(scopePoint, null, scope, entity, null);
            for (int depth = 0; depth < ContainerRegistry.ChainBound && found != null && found.OwnPhysicsFrame; depth++)
            {
                var inner = ContainerRegistry.FindInSpace(PhysicsFrames.Convert(scopePoint, null, found), null, scope, entity, found);
                if (inner == null) break;
                found = inner;
            }
            return found;
        }

        // ------------------------------------------------------------------------------------------ companions

        /// <summary>Whether <paramref name="entity"/> is a companion of a crossing entity this worker still simulates.</summary>
        private bool IsLedCompanion(NetworkIdentity entity)
        {
            if (!_leaderOf.TryGetValue(entity.NetId, out var leaderId)) return false;
            if (_crossings.TryGetValue(leaderId, out var led) && led.Entity != null && led.Entity.IsSpawned && led.Entity.HasAuthority &&
                led.Companions.Contains(entity)) return true;
            _leaderOf.Remove(entity.NetId);
            return false;
        }

        /// <summary>
        /// Ask the game for the entity's companions while it is near the face of an open link. They are asked for every tick the leader is near
        /// rather than once, when it commits: a held item in front of its holder reaches the plane first and, as every entity crosses on its own,
        /// would cross before the holder unless it is already known to be the holder's.
        /// </summary>
        private void PrepareCompanions(NetworkIdentity leader, Crossing crossing)
        {
            Resolved.Clear();
            try { ResolveCompanions(leader, Resolved); }
            catch (Exception e)
            {
                NebulaLog.Error($"Portal '{name}': a ResolveCompanions handler threw: {e}");
                Resolved.Clear();
            }
            // Companions no longer reported are released.
            for (int i = crossing.Companions.Count - 1; i >= 0; i--)
            {
                var companion = crossing.Companions[i];
                if (companion != null && Resolved.Contains(companion)) continue;
                if (companion != null) _leaderOf.Remove(companion.NetId);
                crossing.Companions.RemoveAt(i);
            }
            foreach (var entity in Resolved)
            {
                if (entity == null || entity == leader || crossing.Companions.Contains(entity)) continue;
                // One leader per companion: the first to claim it keeps it while its crossing is live.
                if (_leaderOf.TryGetValue(entity.NetId, out var other) && other != leader.NetId && IsLedCompanion(entity)) continue;
                // An entity crossing with companions of its own is nobody's companion: groups do not nest.
                if (_crossings.TryGetValue(entity.NetId, out var own) && own.Companions.Count > 0) continue;
                _crossings.Remove(entity.NetId);
                _leaderOf[entity.NetId] = leader.NetId;
                crossing.Companions.Add(entity);
            }
            Resolved.Clear();
        }

        /// <summary>Why <paramref name="entity"/> cannot cross with <paramref name="leader"/>, or <see cref="PortalRefusal.None"/> when it can.</summary>
        private static PortalRefusal CompanionProblem(NetworkIdentity entity, NetworkIdentity leader)
        {
            if (entity == null || !entity.IsSpawned || !entity.HasAuthority) return PortalRefusal.NotSimulated;
            if (entity.IsSceneEntity || entity.ContainerPinned) return PortalRefusal.Pinned;
            if (entity.InstanceId != leader.InstanceId) return PortalRefusal.OtherScope;
            return PortalRefusal.None;
        }

        private void ReleaseCompanions(Crossing crossing)
        {
            if (crossing.Companions.Count == 0) return;
            foreach (var companion in crossing.Companions)
                if (companion != null && crossing.Entity != null && _leaderOf.TryGetValue(companion.NetId, out var leader) && leader == crossing.Entity.NetId)
                    _leaderOf.Remove(companion.NetId);
            crossing.Companions.Clear();
        }
    }
}
