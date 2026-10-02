using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Prepares a private area as players approach and crosses its bounds without changing their absolute pose.
    /// <para>
    /// The boundary works in the scope around it, its <see cref="HostInstanceId"/>: the public world when it stands in
    /// the shared scene, or the scope of the container it is parented under, such as a chunk of a scoped grid whose
    /// content the game builds at runtime. Only entities in that scope, or in the boundary's own instance, cross it,
    /// and an occupant who leaves returns to a container of that scope. Positions are compared in absolute
    /// coordinates, so a host scope with an origin frame of its own crosses at the same place on every worker.
    /// </para>
    /// <para>
    /// By default a crossing is prepared whenever a player is near the boundary, and an occupant is prepared to leave
    /// for as long as it is inside. <see cref="RequireRequest"/> and <see cref="Request"/> let the game decide when
    /// instead, for example while a door is open. <see cref="TryGetPreparation"/> and <see cref="CrossingReady"/>
    /// report when a crossing can commit, and <see cref="ExitMargin"/> keeps an occupant standing at the face from
    /// leaving and entering again on every tick. All of it runs on the worker that simulates the entity.
    /// </para>
    /// </summary>
    public sealed class InstanceBoundary : MonoBehaviour
    {
        /// <summary>Private layout created at this component's world position. Must be available on workers.</summary>
        public InstanceTemplate Template;
        /// <summary>Crossing volume relative to the component's position. Rotation and scale do not transform the bounds.</summary>
        public Bounds Interior = new Bounds(Vector3.zero, new Vector3(10, 5, 10));
        /// <summary>Distance added on each side of Interior to begin preparing a crossing before entry.</summary>
        public float PreparationDistance = 8;
        /// <summary>Use the player's stable OwnerIdentity as the key when no ResolveKey callback is installed.</summary>
        public bool Personal = true;
        /// <summary>Key used when Personal is false and no ResolveKey callback is installed.</summary>
        public string SharedKey = "shared";
        /// <summary>Server-side destination key override, for housing assignments or party runs.</summary>
        public Func<NetworkIdentity, string> ResolveKey;
        /// <summary>Server-side admission check for entry into the resolved key. When unset, entry is allowed. Occupants can exit without passing this check.</summary>
        public Func<NetworkIdentity, string, bool> CanEnter;

        /// <summary>
        /// Prepare crossings only on request. When true, being near the boundary prepares nothing: a crossing, in or
        /// out, is prepared only while a <see cref="Request"/> for that entity is live, and an entity that reaches the
        /// face without a ready crossing is held where it last stood. When false, the default, a request only starts
        /// or keeps preparation earlier than proximity would.
        /// </summary>
        public bool RequireRequest;

        /// <summary>
        /// How far outside <see cref="Interior"/>, in meters on each side, an occupant must stand before it leaves the
        /// instance. Entry still happens at <see cref="Interior"/>'s face, so an entity standing at the face does not
        /// leave and enter again on every tick. 0, the default, leaves at the face itself. Within the margin the
        /// occupant is still in the instance and moves against its colliders, so the template's parts and collision
        /// content should cover the margin too.
        /// </summary>
        public float ExitMargin;

        /// <summary>
        /// Raised on this worker once when an entity's crossing becomes ready to commit, before the tick commits it.
        /// It is raised again for a later crossing, after a commit or after the crossing stopped being ready. The
        /// state says which way the crossing leads. Handlers run inside the worker's tick and must not throw.
        /// </summary>
        public event Action<NetworkIdentity, InstanceCrossingState> CrossingReady;

        /// <summary>
        /// The instance origin is this boundary's absolute position rounded to this many meters, so every worker and
        /// every origin frame names the same instance bounds, however its transform's float numbers came out.
        /// </summary>
        public const double OriginResolution = 0.001;

        private sealed class Crossing
        {
            public InstanceTransfer Transfer;
            public Double3 LastPosition;
            public bool HasPosition;
            /// <summary>Why nothing is prepared, when no transfer is in flight.</summary>
            public InstanceCrossingStatus Blocked;
            public bool Leaving;
            /// <summary><see cref="CrossingReady"/> has been raised for the readiness this crossing has now.</summary>
            public bool ReadyRaised;
        }
        private readonly Dictionary<ulong, Crossing> _crossings = new Dictionary<ulong, Crossing>();
        /// <summary>Live requests: when each entity's request expires, in unscaled seconds.</summary>
        private readonly Dictionary<ulong, float> _requests = new Dictionary<ulong, float>();
        private static readonly List<ulong> ExpiredRequests = new List<ulong>();
        private static readonly HashSet<InstanceBoundary> Active = new HashSet<InstanceBoundary>();
        private Container _host;
        private bool _hostResolved;
        private bool _warnedPrepare, _warnedObserve;
        internal Func<float> ClockForTests;
        private float Now => ClockForTests != null ? ClockForTests() : Time.unscaledTime;

        private void OnEnable()
        {
            Active.Add(this);
            _hostResolved = false;
        }

        private void OnDisable() { Active.Remove(this); _crossings.Clear(); _requests.Clear(); }

        private void OnTransformParentChanged() => _hostResolved = false;

        /// <summary>
        /// The scope this boundary stands in: the <see cref="Container.InstanceId"/> of the nearest container among its
        /// parents (a scoped grid's chunk, when the game parents its content under <c>ChunkContext.Root</c>), or 0, the
        /// public world, when it has none.
        /// </summary>
        public ulong HostInstanceId
        {
            get
            {
                // A destroyed container compares equal to null while the reference is still set: look again.
                if (!_hostResolved || (_host == null && !ReferenceEquals(_host, null)))
                {
                    _host = transform.parent != null ? transform.parent.GetComponentInParent<Container>(true) : null;
                    _hostResolved = true;
                }
                return _host != null ? _host.InstanceId : 0;
            }
        }

        /// <summary>This boundary's position in absolute coordinates: its host scope's origin frame taken away.</summary>
        public Double3 AbsolutePosition => ContainerRegistry.ToAbsolutePrecise(transform.position, HostInstanceId);

        /// <summary>Where the instance is prepared: <see cref="AbsolutePosition"/> rounded to <see cref="OriginResolution"/>.</summary>
        public Double3 InstanceOrigin
        {
            get
            {
                var p = AbsolutePosition;
                return new Double3(Round(p.X), Round(p.Y), Round(p.Z));
            }
        }

        private static double Round(double v) => Math.Round(v / OriginResolution) * OriginResolution;

        /// <summary>
        /// Prepare <paramref name="entity"/>'s crossing for the next <paramref name="seconds"/>, in whichever direction
        /// it would cross: into the instance from the host scope, or back out for an occupant. Call it on the worker
        /// that simulates the entity. Each call replaces the entity's previous request; 0 or less withdraws it.
        /// <para>
        /// With <see cref="RequireRequest"/> on, a live request is the only thing that prepares a crossing, and once it
        /// expires a prepared crossing is dropped and can no longer commit. With it off, a request prepares the
        /// crossing even beyond <see cref="PreparationDistance"/>, so it can be ready before the entity arrives.
        /// Either way the entity must still be in the boundary's host scope or its instance, and entry must still
        /// pass <see cref="CanEnter"/>.
        /// </para>
        /// </summary>
        /// <param name="entity">An authoritative entity owned by a player.</param>
        /// <param name="seconds">How long the request lasts, in unscaled seconds.</param>
        public void Request(NetworkIdentity entity, float seconds)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (float.IsNaN(seconds) || seconds <= 0) { _requests.Remove(entity.NetId); return; }
            float now = Now;
            if (_requests.Count >= 16) PruneRequests(now);
            _requests[entity.NetId] = now + seconds;
        }

        /// <summary>
        /// Whether <paramref name="entity"/>'s crossing is prepared and ready to commit, with its
        /// <paramref name="state"/>: which way it leads, and why it is not ready when it is not. The state is the one
        /// the worker's last tick found, with the preparation's progress since then. An entity the boundary is not
        /// preparing a crossing for reports <see cref="InstanceCrossingStatus.None"/>.
        /// </summary>
        /// <param name="entity">An entity this worker simulates.</param>
        /// <param name="state">The crossing's state.</param>
        public bool TryGetPreparation(NetworkIdentity entity, out InstanceCrossingState state)
        {
            if (entity == null || !_crossings.TryGetValue(entity.NetId, out var crossing))
            {
                state = default;
                return false;
            }
            state = StateOf(crossing);
            return state.IsReady;
        }

        private bool IsRequested(ulong netId)
        {
            if (_requests.Count == 0 || !_requests.TryGetValue(netId, out var until)) return false;
            if (Now < until) return true;
            _requests.Remove(netId);
            return false;
        }

        private void PruneRequests(float now)
        {
            ExpiredRequests.Clear();
            foreach (var pair in _requests) if (now >= pair.Value) ExpiredRequests.Add(pair.Key);
            foreach (var id in ExpiredRequests) _requests.Remove(id);
            ExpiredRequests.Clear();
        }

        private static InstanceCrossingState StateOf(Crossing crossing)
        {
            var status = StatusOf(crossing);
            var error = status == InstanceCrossingStatus.Failed ? crossing.Transfer?.Error : null;
            return new InstanceCrossingState(status, crossing.Leaving, error);
        }

        private static InstanceCrossingStatus StatusOf(Crossing crossing)
        {
            if (crossing.Blocked == InstanceCrossingStatus.Refused || crossing.Blocked == InstanceCrossingStatus.NotRequested) return crossing.Blocked;
            var transfer = crossing.Transfer;
            // A transfer finished without an error was committed by someone else: nothing is prepared now.
            if (transfer != null && (!transfer.Finished || transfer.Error != null))
            {
                if (transfer.Error != null) return InstanceCrossingStatus.Failed;
                if (transfer.Ready) return InstanceCrossingStatus.Ready;
                if (!transfer.WorkerReady) return transfer.WorkerContentPending ? InstanceCrossingStatus.PendingContent : InstanceCrossingStatus.PendingWorker;
                return InstanceCrossingStatus.PendingClient;
            }
            return crossing.Blocked == InstanceCrossingStatus.None ? InstanceCrossingStatus.NoDestination : crossing.Blocked;
        }

        /// <summary>An entity's position in absolute coordinates, read in its scope's own space (out of any physics frame).</summary>
        private static Double3 AbsoluteOf(NetworkIdentity entity) =>
            ContainerRegistry.ToAbsolutePrecise(entity.ToScope(entity.transform.position), entity.InstanceId);

        internal static void Tick(NebulaWorker worker, NetworkIdentity entity)
        {
            // A pinned entity keeps its container (docs/frame-bodies.md D8): no boundary moves it into another scope.
            if (entity.OwnerClientId == 0 || !entity.HasAuthority || entity.ContainerPinned) return;
            foreach (var boundary in Active) if (boundary != null) boundary.Cross(worker, entity);
        }

        private void Cross(NebulaWorker worker, NetworkIdentity entity)
        {
            if (Template == null) return;
            string key = ResolveKey != null ? ResolveKey(entity) : Personal ? entity.OwnerIdentity : SharedKey;
            if (string.IsNullOrEmpty(key)) return;
            ulong scope = NebulaWorker.InstanceKey(Template.TemplateId + "/" + key);
            ulong host = HostInstanceId;
            // Only the host scope's entities and this boundary's own occupants: a boundary on one world never catches
            // someone standing at the same numbers on another.
            if (entity.InstanceId != host && entity.InstanceId != scope) return;
            bool privateSide = entity.InstanceId == scope;
            var absolute = AbsoluteOf(entity);
            var local = (absolute - InstanceOrigin).ToVector3();
            bool inside = Interior.Contains(local);
            if (privateSide && !inside && ExitMargin > 0)
            {
                // Leaving takes the margin as well: an occupant just outside the face is still inside.
                var exit = Interior;
                exit.Expand(ExitMargin * 2);
                inside = exit.Contains(local);
            }
            bool requested = IsRequested(entity.NetId);
            var nearby = Interior;
            nearby.Expand(Mathf.Max(0, PreparationDistance) * 2);
            if (!privateSide && !requested && !nearby.Contains(local)) { _crossings.Remove(entity.NetId); return; }
            if (!_crossings.TryGetValue(entity.NetId, out var crossing))
                _crossings.Add(entity.NetId, crossing = new Crossing());
            crossing.Leaving = privateSide;
            bool allowed = privateSide || CanEnter == null || CanEnter(entity, key);
            // Where the entity stands on the other side, in that scope's own frame.
            var there = ContainerRegistry.ToFrame(absolute, privateSide ? host : scope);
            if (RequireRequest && !requested)
            {
                // Nothing is prepared without a request, and a crossing prepared under an expired one is dropped.
                crossing.Transfer = null;
                crossing.Blocked = InstanceCrossingStatus.NotRequested;
            }
            else if (!allowed) crossing.Blocked = InstanceCrossingStatus.Refused;
            else
            {
                // Leaving: a container of the host scope that holds the point, never the nearest box of some other
                // scope. None while that ground is not leased here yet: the occupant waits inside.
                var destination = privateSide ? ContainerRegistry.FindInSpace(there, null, host, entity, null) : PrepareEntry(worker, key, host);
                if (destination == null) crossing.Blocked = InstanceCrossingStatus.NoDestination;
                else if (!LeaseState.IsOwning(destination.LeaseState)) crossing.Blocked = InstanceCrossingStatus.DestinationNotLeased;
                else
                {
                    crossing.Blocked = InstanceCrossingStatus.None;
                    if (crossing.Transfer == null || crossing.Transfer.Finished || crossing.Transfer.Destination != destination)
                        crossing.Transfer = worker.PrepareTransfer(entity, destination);
                }
            }
            if (StatusOf(crossing) != InstanceCrossingStatus.Ready) crossing.ReadyRaised = false;
            else if (!crossing.ReadyRaised)
            {
                crossing.ReadyRaised = true;
                RaiseCrossingReady(entity, crossing);
            }
            if (inside != privateSide)
            {
                if (allowed && worker.TryCommitTransfer(crossing.Transfer, there, entity.transform.rotation))
                {
                    crossing.Transfer = null;
                    crossing.ReadyRaised = false;
                }
                else if (crossing.HasPosition)
                    // Held where it last stood, read back through its scope's frame as it is now: an origin shift
                    // while it waits moves the frame, not the entity.
                    entity.transform.position = entity.FromScope(ContainerRegistry.ToFrame(crossing.LastPosition, entity.InstanceId));
            }
            crossing.LastPosition = AbsoluteOf(entity);
            crossing.HasPosition = true;
        }

        private void RaiseCrossingReady(NetworkIdentity entity, Crossing crossing)
        {
            var handler = CrossingReady;
            if (handler == null) return;
            try { handler(entity, StateOf(crossing)); }
            catch (Exception e) { NebulaLog.Error($"InstanceBoundary '{name}': a CrossingReady handler threw: {e}"); }
        }

        /// <summary>Prepare the instance at this boundary's origin and return its entry part, or null while it isn't there yet or can't be prepared.</summary>
        private Container PrepareEntry(NebulaWorker worker, string key, ulong host)
        {
            if (host != 0 && Template.ObservePublic && !_warnedObserve)
            {
                _warnedObserve = true;
                NebulaLog.Warn($"InstanceBoundary '{name}' stands in a scoped grid, but its template '{Template.TemplateId}' has ObservePublic on: occupants would see the public world, not this scope. Turn ObservePublic off for boundaries in scoped grids, and turn ObserveHost on to keep this scope's ground loaded while occupants are inside.");
            }
            try
            {
                return worker.PrepareInstance(Template, key, InstanceOrigin)[0].Resolve();
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                // A template or key the control plane refuses must not throw out of the worker's tick: nobody crosses.
                if (!_warnedPrepare)
                {
                    _warnedPrepare = true;
                    NebulaLog.Warn($"InstanceBoundary '{name}' could not prepare '{Template.TemplateId}/{key}': {e.Message}");
                }
                return null;
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(transform.position + Interior.center, Interior.size);
            if (ExitMargin > 0)
            {
                Gizmos.color = new Color(0f, 1f, 1f, 0.35f);
                Gizmos.DrawWireCube(transform.position + Interior.center, Interior.size + Vector3.one * (ExitMargin * 2));
            }
        }
    }
}
