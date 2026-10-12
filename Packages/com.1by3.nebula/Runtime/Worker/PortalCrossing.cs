using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Crossing, on the worker that simulates the entity and owns the portal (<c>docs/portals.md</c> D14 to D17). A link that is open has
    /// its far side ready already, so a crossing prepares and loads nothing: the entity is committed on the tick its root passes the
    /// face. Today only player-owned pawns cross by themselves, and the test is the segment from last tick's position to this tick's;
    /// every other entity and the predictive test for fast bodies are NEB-429's.
    /// </summary>
    public sealed partial class Portal
    {
        private sealed class Crossing
        {
            public NetworkIdentity Entity;
            /// <summary>Where the entity stood last tick, absolute in the scope it was in then.</summary>
            public Double3 Last;
            public ulong LastScope;
            public bool HasLast;
            /// <summary>What crosses with it (<see cref="ResolveCompanions"/>).</summary>
            public readonly List<NetworkIdentity> Companions = new List<NetworkIdentity>();
        }

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

        private PortalFace NearFaceOf(NebulaWorker worker)
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

        private PortalLink LinkByFarScope(ulong scope)
        {
            foreach (var link in _links.Values)
                if (link.FarScope == scope && link.FarScope != 0 && link.State != PortalState.Closed) return link;
            // A link that closed under an occupant still owns the scope it leads to: the occupant meets the aperture from inside.
            foreach (var link in _links.Values) if (link.FarScope == scope && link.FarScope != 0) return link;
            return null;
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

        private bool Admits(NetworkIdentity entity, PortalLink link)
        {
            if (CanEnter != null)
            {
                try { return CanEnter(entity, link); }
                catch (Exception e) { NebulaLog.Error($"Portal '{name}': a CanEnter handler threw: {e}"); return false; }
            }
            return entity.OwnerClientId == 0 || (link.IsMember(entity.OwnerClientId) && link.IsWarm(entity.OwnerClientId));
        }

        private void PruneCrossings()
        {
            StaleScratch.Clear();
            foreach (var kv in _crossings)
                if (kv.Value.Entity == null || !kv.Value.Entity.IsSpawned || !kv.Value.Entity.HasAuthority) StaleScratch.Add(kv.Key);
            for (int i = 0; i < StaleScratch.Count; i++)
            {
                if (_crossings.TryGetValue(StaleScratch[i], out var gone)) ReleaseCompanions(gone);
                _crossings.Remove(StaleScratch[i]);
            }
            StaleScratch.Clear();
        }

        /// <summary>Test one entity against this portal. Called by <see cref="Portals.Tick"/> for entities this worker simulates.</summary>
        internal void Cross(NebulaWorker worker, NetworkIdentity entity)
        {
            if (_leaderOf.Count > 0 && IsLedCompanion(entity)) return;
            var near = NearContainer;
            if (near == null || near.OwnerWorkerId != worker.WorkerId) return;
            ulong scope = entity.InstanceId;
            var nearFace = NearFaceOf(worker);
            PortalLink link;
            PortalFace face;
            bool leaving;
            var refusal = PortalRefusal.None;
            if (scope == nearFace.Scope)
            {
                leaving = false;
                face = nearFace;
                link = ResolveLink(entity, out refusal);
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
            bool inZone = Math.Abs(local.X) <= Aperture.HalfWidth + ApproachDistance && Math.Abs(local.Y) <= Aperture.HalfHeight + ApproachDistance &&
                          Math.Abs(local.Z) <= ApproachDistance;
            if (!inZone) { Forget(entity); return; }
            if (!_crossings.TryGetValue(entity.NetId, out var crossing)) _crossings.Add(entity.NetId, crossing = new Crossing());
            crossing.Entity = entity;
            if (crossing.HasLast && crossing.LastScope != scope) crossing.HasLast = false;

            if (link != null && link.State == PortalState.Open) PrepareCompanions(entity, crossing, link, leaving);
            else ReleaseCompanions(crossing);

            if (crossing.HasLast && Passes(face.ToLocal(crossing.Last), local, leaving))
            {
                if (!TryCross(worker, entity, crossing, link, leaving, absolute, refusal))
                {
                    // Held where it last stood, read back through its scope's frame as it is now: an origin shift while it waits moves the frame,
                    // not the entity. (The clamp against the plane that replaces this is NEB-429's.)
                    entity.transform.position = entity.FromScope(ContainerRegistry.ToFrame(crossing.Last, scope));
                    return;
                }
                crossing.Last = AbsoluteOf(entity);
                crossing.LastScope = entity.InstanceId;
                crossing.HasLast = true;
                return;
            }
            crossing.Last = absolute;
            crossing.LastScope = scope;
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
        /// that keeps an entity at the face from crossing and returning on every tick (<c>docs/portals.md</c> D15).
        /// </summary>
        private bool Passes(Double3 last, Double3 now, bool leaving)
        {
            double plane = leaving ? -CrossDepth : CrossDepth;
            bool through = leaving ? last.Z > plane && now.Z <= plane : last.Z < plane && now.Z >= plane;
            if (!through) return false;
            double t = (plane - last.Z) / (now.Z - last.Z);
            return Aperture.Contains(last.X + t * (now.X - last.X), last.Y + t * (now.Y - last.Y));
        }

        private bool TryCross(NebulaWorker worker, NetworkIdentity entity, Crossing crossing, PortalLink link, bool leaving, Double3 absolute, PortalRefusal refusal)
        {
            if (link == null) { Refuse(refusal == PortalRefusal.None ? PortalRefusal.LinkNotOpen : refusal); return false; }
            if (link.State != PortalState.Open) { Refuse(PortalRefusal.LinkNotOpen); return false; }
            if (!leaving && !Admits(entity, link)) { Refuse(PortalRefusal.NotAdmitted); return false; }

            ulong thereScope = leaving ? NearFaceOf(worker).Scope : link.FarScope;
            var thereAbsolute = leaving ? link.ToNear(absolute) : link.ToFar(absolute);
            Container destination;
            if (leaving)
            {
                // A container of the near scope that holds the point, never the nearest box of some other scope; none while that ground is not
                // leased here yet.
                destination = FindDestination(ContainerRegistry.ToFrame(thereAbsolute, thereScope), thereScope, entity);
            }
            else destination = link.FarEntry;
            if (destination == null || !LeaseState.IsOwning(destination.LeaseState)) { Refuse(PortalRefusal.NoDestination); return false; }
            if (!leaving && destination.LeaseEpoch != link.ReadyEpoch) { Refuse(PortalRefusal.NoDestination); return false; }

            // The leader and the companions that can cross with it.
            Group.Clear();
            Group.Add(entity);
            foreach (var companion in crossing.Companions)
            {
                var problem = CompanionProblem(companion, entity);
                if (problem == PortalRefusal.None && !leaving && !Admits(companion, link)) problem = PortalRefusal.NotAdmitted;
                if (problem == PortalRefusal.None) Group.Add(companion);
                else ReportLeftBehind(entity, companion, problem);
            }
            var turnAcross = leaving ? link.Turn : Quaternion.Inverse(link.Turn);
            bool turns = Quaternion.Angle(Quaternion.identity, turnAcross) > NebulaWorker.PoseAngleEpsilon;
            var origin = ContainerRegistry.ToAbsolutePrecise(Double3.Zero, thereScope);
            var positions = new Double3[Group.Count];
            var rotations = new Quaternion[Group.Count];
            var velocities = new Vector3[Group.Count];
            for (int i = 0; i < Group.Count; i++)
            {
                var member = Group[i];
                var memberAbsolute = i == 0 ? absolute : AbsoluteOf(member);
                positions[i] = (leaving ? link.ToNear(memberAbsolute) : link.ToFar(memberAbsolute)) - origin;
                rotations[i] = turnAcross * ScopeRotationOf(member);
                velocities[i] = member.Motion.Velocity;
                if (turns) member.Motion.Velocity = turnAcross * velocities[i];
            }
            var members = new List<NetworkIdentity>(Group);
            Group.Clear();
            if (!worker.CommitThrough(members, destination, positions, rotations))
            {
                for (int i = 0; i < members.Count; i++) members[i].Motion.Velocity = velocities[i];
                Refuse(PortalRefusal.NoDestination);
                return false;
            }
            ReleaseCompanions(crossing);
            foreach (var member in members) RaiseCrossed(member, link, leaving);
            return true;
        }

        private static void Refuse(PortalRefusal why) => NebulaDiagnostics.PortalRefusals++;

        private void RaiseCrossed(NetworkIdentity entity, PortalLink link, bool leaving)
        {
            var handler = Crossed;
            if (handler == null) return;
            try { handler(entity, link, leaving); }
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

        /// <summary>Ask the game for the entity's companions while it is near the face of an open link.</summary>
        private void PrepareCompanions(NetworkIdentity leader, Crossing crossing, PortalLink link, bool leaving)
        {
            Resolved.Clear();
            if (ResolveCompanions != null)
            {
                try { ResolveCompanions(leader, Resolved); }
                catch (Exception e)
                {
                    NebulaLog.Error($"Portal '{name}': a ResolveCompanions handler threw: {e}");
                    Resolved.Clear();
                }
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
