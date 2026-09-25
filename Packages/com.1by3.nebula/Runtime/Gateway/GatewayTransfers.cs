using System;
using System.Collections.Generic;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// Crossings, seen from the gateway: an entity, or a whole crewed carrier, moving into a container of another
    /// scope. Five rules, each a decision in <c>docs/scope-activation.md</c> §11:
    /// <list type="bullet">
    /// <item>a preparation brings its destination row with it, because the client that must prepare the destination
    /// has never been told it exists (D14);</item>
    /// <item>an update naming a runtime container this gateway has no row for yet waits for the row (D15);</item>
    /// <item>a client follows its pawn's carriers by name, so a ship that leaves the client's scope is still heard
    /// about (D12);</item>
    /// <item>everything aboard those carriers is followed by name too, so it crosses with them and is never
    /// despawned for a client riding in them (D21);</item>
    /// <item>an entity that changes scope is revoked from the observers that may no longer see it before anything
    /// about the destination is sent (D13).</item>
    /// </list>
    /// </summary>
    public sealed partial class NebulaGateway
    {
        /// <summary>How long a destination row stays pinned for a client after a preparation named it.</summary>
        public const double PreparedRowSeconds = 30;
        /// <summary>
        /// How long a container row that has left a client's window, but whose lease still exists, is kept before it
        /// is withdrawn. Longer than any interpolation delay: a replica gliding out of a box through its buffer is
        /// still expressed in that box's frame, and the client despawns what stands in a withdrawn row.
        /// </summary>
        public const double ContainerRowLingerSeconds = 1;
        /// <summary>
        /// The longest an entity's updates wait for a container row this gateway does not have. The control plane
        /// delivers a new lease in one long-poll round; past this bound the updates are applied anyway and the
        /// entity fails closed exactly as an unknown container always did.
        /// </summary>
        public const double HoldSeconds = 10;
        /// <summary>Most updates held for one entity; reaching it releases the hold at once (the same fail-closed path).</summary>
        private const int HoldLimit = 1024;
        /// <summary>How long a preparation whose destination row has not arrived waits here; the worker's own timeout is 15 s.</summary>
        private const double PreparationHoldSeconds = 15;

        // ------------------------------------------------------------------------------------------- preparations

        private struct PendingPreparation
        {
            public ulong ClientId;
            public InstancePreparationMsg Message;
            public double Deadline;
        }

        private readonly List<PendingPreparation> _pendingPreparations = new List<PendingPreparation>();

        /// <summary>
        /// A worker asks the client that owns a pawn to prepare the pawn's destination. The client can only answer
        /// for a container it has a row for, and a client is told only about the containers of its own scope
        /// (<c>docs/scoped-chunk-grids.md</c> D10), so a destination in <b>another</b> scope was, before this,
        /// always "unavailable" and a crossing between two grid scopes never became ready. The destination's row
        /// now goes to that one client, on the same reliable stream and ahead of the request, and stays pinned
        /// while the crossing can still commit (D14). Nobody else is told: the worker simulating the pawn is the
        /// authority asking, and the pawn is the client's own.
        /// </summary>
        private void OnInstancePrepare(WorkerConn w, InstancePreparationMsg preparation)
        {
            if (!_entities.TryGetValue(preparation.EntityId, out var pawn) || pawn.OwnerWorkerIndex != w.Index ||
                !_clientsById.TryGetValue(pawn.OwnerClientId, out var client) || client.PawnNetId != pawn.NetId) return;
            preparation.SourceWorker = w.Index;
            var pending = new PendingPreparation { ClientId = client.ClientId, Message = preparation, Deadline = InterestNow + PreparationHoldSeconds };
            if (TryRelayPreparation(pending)) return;
            // The worker's control-plane mirror is ahead of ours: it knows a destination lease we have not been
            // given yet. Wait for it rather than send the client a request it cannot answer.
            NebulaLog.Debugf($"preparation of #{preparation.EntityId} names {preparation.Destination}, which this gateway has no row for yet; waiting for it");
            _pendingPreparations.Add(pending);
        }

        /// <summary>Send the destination row and then the preparation; false while the row is not here (or is older than the request).</summary>
        private bool TryRelayPreparation(in PendingPreparation pending)
        {
            if (!_clientsById.TryGetValue(pending.ClientId, out var client) || !client.Welcomed) return true; // nobody left to ask
            var destination = ContainerRegistry.Resolve(pending.Message.Destination);
            if (destination == null || string.IsNullOrEmpty(destination.ContainerId) ||
                !_ownershipById.TryGetValue(destination.ContainerId, out var row) || row.Epoch < pending.Message.LeaseEpoch) return false;
            string id = destination.ContainerId;
            client.PreparedRows[id] = InterestNow + PreparedRowSeconds;
            client.RowsLeaving.Remove(id);
            if (client.KnownContainers.Add(id))
            {
                _upsertScratch.Clear();
                _upsertScratch.Add(row);
                _interestWriter.Reset();
                ContainerOwnershipMsg.Write(_interestWriter, _upsertScratch, false, null);
                AppendReliable(client, _interestWriter.ToSegment());
            }
            _interestWriter.Reset();
            pending.Message.Write(_interestWriter, MsgId.InstancePrepare);
            AppendReliable(client, _interestWriter.ToSegment());
            return true;
        }

        private void RetryPreparations(double now)
        {
            for (int i = _pendingPreparations.Count - 1; i >= 0; i--)
            {
                var pending = _pendingPreparations[i];
                if (TryRelayPreparation(pending)) { _pendingPreparations.RemoveAt(i); continue; }
                if (now < pending.Deadline) continue;
                NebulaLog.Warn($"preparation of #{pending.Message.EntityId}: no row for {pending.Message.Destination} arrived in {PreparationHoldSeconds} s; the crossing will time out on its worker");
                _pendingPreparations.RemoveAt(i);
            }
        }

        /// <summary>Whether a preparation still pins this row for this client (expired pins are dropped here).</summary>
        private bool IsPrepared(ClientConn client, string containerId, double now)
        {
            if (!client.PreparedRows.TryGetValue(containerId, out double until)) return false;
            if (now < until) return true;
            client.PreparedRows.Remove(containerId);
            return false;
        }

        // ------------------------------------------------------------------------------------------- held updates

        /// <summary>What a held update is.</summary>
        private enum HeldKind : byte { Spawn, State, Vars, Sync, Rpc, Despawn, Audience }

        /// <summary>
        /// One update held for an entity: a spawn or state entry that named a container this gateway could not
        /// describe when it arrived, or a variable, behaviour-state, RPC or despawn message that arrived behind one.
        /// </summary>
        private struct HeldUpdate
        {
            public HeldKind Kind;
            public EntitySpawnMsg Spawn;
            public EntityStateEntry Entry;
            public EntityVarsMsg Vars;
            public EntitySyncMsg Sync;
            public EntityRpcMsg Rpc;
            public EntityDespawnMsg Despawn;
            public SyncAudienceMsg Audience;
            public uint Tick;
            public ushort Worker;

            /// <summary>
            /// The container this update waits for: a spawn's or a state entry's. Variables, behaviour state and
            /// RPCs wait for none of their own; they only keep their place behind what is held ahead of them.
            /// </summary>
            public ContainerRef WaitsFor => Kind == HeldKind.Spawn ? Spawn.Container : Kind == HeldKind.State ? Entry.Container : ContainerRef.None;

            public uint Epoch => Kind switch
            {
                HeldKind.Spawn => Spawn.Epoch,
                HeldKind.State => Entry.Epoch,
                HeldKind.Vars => Vars.Epoch,
                HeldKind.Sync => Sync.Epoch,
                HeldKind.Rpc => Rpc.Epoch,
                HeldKind.Audience => Audience.Epoch,
                _ => Despawn.Epoch,
            };
        }

        private sealed class HeldUpdates
        {
            public double Since;
            /// <summary>
            /// The oldest epoch held. An owner message of an older epoch predates everything held (a previous
            /// owner's last words before a handover) and is applied at once, as it always was.
            /// </summary>
            public uint Epoch;
            public readonly List<HeldUpdate> Items = new List<HeldUpdate>();

            public void Add(in HeldUpdate update)
            {
                if (Items.Count == 0 || update.Epoch < Epoch) Epoch = update.Epoch;
                Items.Add(update);
                if (Items.Count >= HoldLimit) Since = double.NegativeInfinity;
            }

            /// <summary>Forget the first <paramref name="count"/> items, which are being applied.</summary>
            public void Released(int count, double now)
            {
                Items.RemoveRange(0, count);
                Since = now;
                if (Items.Count == 0) return;
                Epoch = Items[0].Epoch;
                for (int i = 1; i < Items.Count; i++) if (Items[i].Epoch < Epoch) Epoch = Items[i].Epoch;
            }
        }

        /// <summary>Per entity, the updates waiting for a container row, in arrival order.</summary>
        private readonly Dictionary<ulong, HeldUpdates> _held = new Dictionary<ulong, HeldUpdates>();
        private readonly List<ulong> _heldScratch = new List<ulong>();
        private readonly List<HeldUpdate> _releaseScratch = new List<HeldUpdate>();
        /// <summary>Set while held updates are being applied, so they go through instead of being held again.</summary>
        private bool _replayingHeld;

        /// <summary>
        /// A runtime container reference this gateway has no registry entry for — the lease row has not reached
        /// its control-plane mirror yet. Static and dynamic references are never "undescribed" here: a static
        /// container is baked into every process, and a missing carrier is followed by name (D12).
        /// </summary>
        private static bool Undescribed(ContainerRef container) =>
            container.Index == ContainerRef.RuntimeIndex && ContainerRegistry.Resolve(container) == null;

        /// <summary>
        /// Hold <paramref name="update"/> when it names a container this gateway cannot describe, or when earlier
        /// updates of the same entity are already held (order is kept). A worker leases a chunk and moves an entity
        /// into it faster than the lease reaches this gateway's mirror; applying the update now would make every
        /// decision — its scope, its region key, who may see it — on a container nobody here knows, and the
        /// fail-closed answer to that is to take the entity away from everyone who holds it (D15).
        /// </summary>
        private bool HoldIfUndescribed(ulong netId, ContainerRef container, in HeldUpdate update)
        {
            if (_replayingHeld) return false;
            bool holding = _held.TryGetValue(netId, out var held);
            if (!holding && !Undescribed(container)) return false;
            if (!holding)
            {
                held = new HeldUpdates { Since = InterestNow };
                _held[netId] = held;
                NebulaLog.Debugf($"entity #{netId} names {container}, which this gateway has no row for yet; holding its updates until the row arrives");
            }
            held.Add(update);
            return true;
        }

        /// <summary>
        /// Hold a variable, behaviour-state, RPC or despawn message behind the updates already held for its entity.
        /// While a new owner's spawn waits for its row, the record still names the previous owner (or, after a
        /// redirect, an owner whose spawn has not been applied): the ordinary owner check would drop the new
        /// owner's messages, or relay them ahead of the spawn, whose older variables would then overwrite them.
        /// Either way the entity would arrive with stale state until its next change. Held here, they are applied
        /// through the normal path, in arrival order, right after the spawn lands. A message of an epoch older than
        /// everything held is not held (<see cref="HeldUpdates.Epoch"/>). The bound is the rest of the hold's:
        /// <see cref="HoldSeconds"/> and <see cref="HoldLimit"/> updates per entity, counted together.
        /// </summary>
        private bool HoldBehind(ulong netId, uint epoch, in HeldUpdate update)
        {
            if (_replayingHeld || !_held.TryGetValue(netId, out var held) || epoch < held.Epoch) return false;
            held.Add(update);
            return true;
        }

        /// <summary>
        /// Apply every held update whose container can be described now, in order, stopping at the first that still
        /// cannot. Past <see cref="HoldSeconds"/> everything is applied regardless. State entries and behaviour
        /// state released here go out on the reliable stream, behind the row they name and the spawn they follow.
        /// </summary>
        private void ReleaseHeldUpdates(double now)
        {
            if (_held.Count == 0) return;
            _heldScratch.Clear();
            foreach (var id in _held.Keys) _heldScratch.Add(id);
            for (int n = 0; n < _heldScratch.Count; n++)
            {
                ulong netId = _heldScratch[n];
                if (!_held.TryGetValue(netId, out var held)) continue;
                bool expired = now - held.Since >= HoldSeconds;
                int ready = 0;
                while (ready < held.Items.Count && (expired || !Undescribed(held.Items[ready].WaitsFor))) ready++;
                if (ready == 0) continue;
                if (expired) NebulaLog.Warn($"entity #{netId}: no row for {held.Items[0].WaitsFor} arrived in {HoldSeconds} s; applying its updates anyway");
                _releaseScratch.Clear();
                for (int i = 0; i < ready; i++) _releaseScratch.Add(held.Items[i]);
                held.Released(ready, now);
                if (held.Items.Count == 0) _held.Remove(netId);
                _replayingHeld = true;
                try
                {
                    for (int i = 0; i < _releaseScratch.Count; i++) ApplyHeld(_releaseScratch[i]);
                }
                finally { _replayingHeld = false; }
                _releaseScratch.Clear();
            }
            _heldScratch.Clear();
        }

        private void ApplyHeld(in HeldUpdate update)
        {
            // The worker that sent it is gone: whatever it said is superseded by the loss handling.
            if (!_workersByIndex.TryGetValue(update.Worker, out var w)) return;
            switch (update.Kind)
            {
                case HeldKind.Spawn: OnEntitySpawn(w, update.Spawn); return;
                case HeldKind.Vars: OnEntityVars(w, update.Vars); return;
                case HeldKind.Rpc: OnEntityRpc(w, update.Rpc); return;
                case HeldKind.Despawn: OnEntityDespawn(w, update.Despawn); return;
                case HeldKind.Audience: OnSyncAudience(w, update.Audience); return;
                case HeldKind.Sync:
                {
                    // Behind the spawn it waited for, on the same stream: a sequenced copy could overtake it.
                    var sync = update.Sync;
                    sync.Reliable = true;
                    OnEntityState(w, sync);
                    return;
                }
            }
            if (!_entities.TryGetValue(update.Entry.NetId, out var rec)) return;
            var entry = update.Entry;
            if (!ApplyStateEntry(w, update.Tick, rec, ref entry)) return;
            RelayReliable(rec, update.Tick, w.Index, entry);
        }

        // ------------------------------------------------------------------------------------------- scope changes

        /// <summary>The isolation id a reference resolves to through its carriers; null when the chain cannot be resolved.</summary>
        private ulong? ScopeIdOf(ContainerRef reference)
        {
            if (reference.IsNone) return 0UL;
            var scope = ScopeContainer(reference);
            return scope != null ? scope.InstanceId : (ulong?)null;
        }

        /// <summary>
        /// The scope a client is in: its pawn's, through the pawn's carrier chain. While that chain cannot be
        /// resolved — the carrier's record is on its way (D12) — the client stays in the scope it was last known to
        /// be in instead of falling back to the public world, which would widen what it may see and salt its
        /// subscriptions with the wrong world (D16). A client with no pawn yet is in the scope its Hello named.
        /// </summary>
        private ulong ScopeOfClient(ClientConn client)
        {
            if (client.PawnNetId != 0 && _entities.TryGetValue(client.PawnNetId, out var pawn))
            {
                var scope = ScopeIdOf(pawn.Container);
                if (scope.HasValue) client.LastScope = scope.Value;
            }
            return client.LastScope;
        }

        private readonly List<ClientConn> _scopeRevokeScratch = new List<ClientConn>();

        /// <summary>
        /// An entity moved into another scope: re-authorize every client that holds it, now. The ones that may not
        /// see it in its new scope lose it (and whatever rides in it) before this entity's next message is relayed,
        /// and before the destination container's row is sent to anyone — an onlooker watching a ship leave the
        /// planet is never told which chunk of space it went to (D13). The ones that may (its crew, whose own scope
        /// follows the ship) keep it with no despawn at all.
        /// </summary>
        private void RevokeAcrossScope(EntityRecord rec)
        {
            if (rec.Observers.Count == 0) return;
            _scopeRevokeScratch.Clear();
            _scopeRevokeScratch.AddRange(rec.Observers);
            for (int i = 0; i < _scopeRevokeScratch.Count; i++) RevalidateInterest(_scopeRevokeScratch[i]);
            _scopeRevokeScratch.Clear();
        }

        private readonly List<ulong> _riderScratch = new List<ulong>();

        /// <summary>Every client whose pawn rides in this carrier's subtree is now in the carrier's new scope.</summary>
        private void MarkRidersDirty(EntityRecord carrier)
        {
            if (!_index.HasCarried(carrier.NetId)) return;
            _riderScratch.Clear();
            int count = _index.CollectCarried(carrier.NetId, _riderScratch);
            for (int i = 0; i < count; i++)
            {
                if (!_entities.TryGetValue(_riderScratch[i], out var rider) || rider.OwnerClientId == 0) continue;
                if (_clientsById.TryGetValue(rider.OwnerClientId, out var client) && client.PawnNetId == rider.NetId) client.InterestDirty = true;
            }
            _riderScratch.Clear();
        }

        // ------------------------------------------------------------------------------------------- following carriers

        /// <summary>Every explicit id of this subscription pass, as a set: the eviction sweep asks it once per record.</summary>
        private readonly HashSet<ulong> _explicitSet = new HashSet<ulong>();

        private void AddExplicit(ulong netId)
        {
            if (netId != 0 && _explicitSet.Add(netId)) _explicitIds.Add(netId);
        }

        /// <summary>
        /// Follow a pawn's carriers by name. A client's scope is its pawn's, and a carried pawn's scope is its
        /// outermost carrier's (<see cref="ScopeContainer"/>). The gateway learns that a carrier moved only from
        /// the carrier's own updates, and a carrier that crossed into another scope is published under that scope's
        /// region keys (<c>docs/scope-frames.md</c> D7) — which nobody subscribes for this client, because its
        /// subscriptions are salted from the scope the carrier record still says it is in. Naming every carrier in
        /// the chain explicitly makes each one sticky on its worker for this gateway, wherever it goes, so the
        /// crossing arrives, the client's scope follows it, and its window and subscriptions follow that (D12).
        /// A carrier this gateway has no record of at all (a reconnect after the crossing) is named too, which is
        /// what links every live worker until the one holding it answers.
        /// </summary>
        private void FollowCarriers(EntityRecord pawn)
        {
            var at = pawn.Container;
            EntityRecord outermost = null;
            for (int hops = 0; TryCarrierOf(at, out ulong carrierNetId, out _) && hops <= _entities.Count; hops++)
            {
                AddExplicit(carrierNetId);
                if (!_entities.TryGetValue(carrierNetId, out var carrier)) break;
                outermost = carrier;
                string owner = WorkerIdOfIndex(carrier.OwnerWorkerIndex);
                if (!string.IsNullOrEmpty(owner) && EnsureLink(owner) != null) Reason(owner, InterestLinkReason.Owned);
                at = carrier.Container;
            }
            if (outermost != null && !_followedCarriers.Contains(outermost.NetId)) _followedCarriers.Add(outermost.NetId);
        }

        /// <summary>The outermost carrier of every pawn chain followed this subscription pass, in the order first met.</summary>
        private readonly List<ulong> _followedCarriers = new List<ulong>();
        private readonly List<ulong> _passengerScratch = new List<ulong>();

        /// <summary>
        /// Most passengers named by <see cref="FollowPassengers"/> in one subscription pass, across all the carriers
        /// this gateway follows. It keeps the explicit list of an <see cref="InterestSubscribeMsg"/> well inside its
        /// count field; cargo past it is served as before, by region, and may flicker on a scope change.
        /// </summary>
        public const int MaxFollowedPassengers = 4096;

        /// <summary>
        /// Follow everything aboard a followed carrier by name too, at any depth: loose cargo, a shuttle in the bay
        /// and what is in it, another gateway's rider. A worker publishes a carrier's contents under the carrier's
        /// region key, and a crossing into another scope rebuckets them all under a key of that scope. The carrier
        /// itself is sticky for this gateway because it is named (D12); its contents were not, so the worker told
        /// this gateway to forget each of them, and the riders' clients despawned the cargo, and were sent it again
        /// once their subscriptions caught up with the new scope. Named, the contents are carried across with the
        /// carrier: never forgotten by the worker, never evicted here, and never despawned for a client that rides
        /// in the carrier, whose scope follows it. Naming grants no visibility: an onlooker left in the old scope is
        /// still revoked from the carrier and everything in it before anything about the destination is sent (D13).
        /// Named after every carrier, so the carriers themselves are never the ones <see cref="MaxFollowedPassengers"/>
        /// leaves out (D21).
        /// </summary>
        private void FollowPassengers()
        {
            int named = 0;
            for (int i = 0; i < _followedCarriers.Count && named < MaxFollowedPassengers; i++)
            {
                ulong carrier = _followedCarriers[i];
                if (!_index.HasCarried(carrier)) continue;
                _passengerScratch.Clear();
                int count = _index.CollectCarried(carrier, _passengerScratch);
                for (int p = 0; p < count && named < MaxFollowedPassengers; p++)
                {
                    ulong id = _passengerScratch[p];
                    if (_explicitSet.Contains(id) || !_entities.TryGetValue(id, out var passenger)) continue;
                    // Owned by a client of this gateway (a rider's own pawn): already sticky here as its owner's,
                    // and a worker announces a newly named entity, which for an owned one would read as a new pawn.
                    if (passenger.OwnerClientId != 0 && _clientsById.ContainsKey(passenger.OwnerClientId)) continue;
                    AddExplicit(id);
                    named++;
                }
            }
            _passengerScratch.Clear();
            _followedCarriers.Clear();
        }
    }
}
