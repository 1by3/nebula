using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>A prepared crossing. It can be committed once, while the source authority and destination lease remain valid.</summary>
    public sealed class InstanceTransfer
    {
        /// <summary>Whether worker and client preparation succeeded. Commit still rechecks authority and the destination lease.</summary>
        public bool Ready => !Finished && ClientReady && WorkerReady && Error == null;
        /// <summary>True after a successful commit, expiration, or invalidation. Finished preparations cannot be reused.</summary>
        public bool Finished { get; internal set; }
        /// <summary>Failure description, or null when no failure has been recorded. Inspect independently of Finished.</summary>
        public string Error { get; internal set; }
        /// <summary>Static container selected when preparation began.</summary>
        public Container Destination { get; internal set; }
        /// <summary>
        /// Why the preparation was refused, in typed form: <see cref="JoinRejectReason.AtCapacity"/> when the
        /// destination was at capacity and the admission hook refused this crossing, and
        /// <see cref="JoinRejectReason.Denied"/> when the hook refused a destination that was not
        /// (<see cref="NebulaAdmission"/>, docs/capacity-admission.md). <see cref="JoinRejectReason.None"/> for
        /// every other failure, which <see cref="Error"/> describes.
        /// </summary>
        public JoinRejectReason RejectReason { get; internal set; }
        /// <summary>How saturated the destination was when it was refused; 0 when that was not the reason.</summary>
        public float Saturation { get; internal set; }
        internal NetworkIdentity Entity;
        internal Container Source;
        internal uint Epoch;
        internal InstancePreparationMsg Message;
        internal bool ClientReady, WorkerReady;
        internal float Deadline;
    }

    public sealed partial class NebulaWorker
    {
        private uint _nextInstanceRequest;
        private readonly Dictionary<uint, InstanceTransfer> _instanceTransfers = new Dictionary<uint, InstanceTransfer>();
        private readonly List<uint> _expiredInstanceRequests = new List<uint>();

        /// <summary>Stable identifier for an instance or one of its containers. Include a run ID in the key for temporary instances.</summary>
        /// <remarks>The derivation itself is <see cref="ScopeKeys.Hash"/>, so a gateway, an orchestrator or a
        /// matchmaking service can name the same scope without a worker and without Unity.</remarks>
        public static ulong InstanceKey(string key) => ScopeKeys.Hash(key);

        /// <summary>Create or find stable instance container leases. Resolve the returned references after the control-plane update. This does not automatically persist entity state.</summary>
        /// <param name="template">Layout with a stable ID, unique part IDs, and positive-size bounds.</param>
        /// <param name="key">Game-selected home, party, or run key. The same template ID and key select the same scope.</param>
        /// <param name="origin">Instance origin in this process's world frame. Existing keys must retain their bounds and content resource.</param>
        public ContainerRef[] PrepareInstance(InstanceTemplate template, string key, Vector3 origin)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("An instance key is required", nameof(key));
            if (template == null || string.IsNullOrEmpty(template.TemplateId) || template.Parts == null || template.Parts.Length == 0) throw new ArgumentException("An instance template needs an ID and containers");
            string prefix = template.TemplateId + "/" + key;
            ulong scope = InstanceKey(prefix);
            var references = new ContainerRef[template.Parts.Length];
            var ids = new HashSet<string>();
            for (int i = 0; i < template.Parts.Length; i++)
            {
                var part = template.Parts[i];
                if (part == null || string.IsNullOrEmpty(part.Id) || !ids.Add(part.Id) || part.Bounds.size.x <= 0 || part.Bounds.size.y <= 0 || part.Bounds.size.z <= 0)
                    throw new ArgumentException("Instance parts need unique IDs and positive bounds");
            }
            // An instance is a scope: the template and the origin become a ScopeDefinition, and preparing it is the
            // same ActivateScope a matchmaking service or a travel menu would call from outside the mesh
            // (docs/scope-activation.md D2). PreferredWorkerId keeps the old behaviour that the worker that asked
            // owns the new containers from the first change anyone sees.
            var view = ContainerRegistry.ToAbsolute(new Bounds(origin + template.PublicView.center, template.PublicView.size));
            var definition = new ScopeDefinition
            {
                Kind = ScopeKind.Parts,
                ObservePublic = template.ObservePublic,
                ObservationCenter = view.center,
                ObservationSize = view.size,
            };
            for (int i = 0; i < template.Parts.Length; i++)
            {
                var part = template.Parts[i];
                ulong id = InstanceKey(prefix + "/" + part.Id);
                references[i] = ContainerRef.Runtime(id);
                var bounds = ContainerRegistry.ToAbsolute(new Bounds(origin + part.Bounds.center, part.Bounds.size));
                definition.Parts.Add(new ScopePart { PartId = part.Id, Center = bounds.center, Size = bounds.size, ContentResource = part.ContentResource ?? "" });
                var existing = ControlPlane.FindLease(ContainerRegistry.RuntimeContainerId(id));
                if (existing != null && (existing.Instance?.InstanceId != scope || existing.Bounds != bounds || existing.Instance.ContentResource != part.ContentResource))
                    throw new InvalidOperationException("Instance key already names different content or bounds");
                // The stored scope key is the contract's key for this scope (EntityLocation.ScopeKey). A row written
                // before the key was recorded has none and stands; a row with a different key under the same hash is
                // a collision and is refused rather than silently merged.
                if (existing != null && !string.IsNullOrEmpty(existing.Instance.ScopeKey) && !string.Equals(existing.Instance.ScopeKey, prefix, StringComparison.Ordinal))
                    throw new InvalidOperationException("Instance key collides with an existing scope key: " + existing.Instance.ScopeKey);
            }
            ControlPlane.ActivateScope(new ScopeActivationRequest
            { ScopeKey = prefix, Definition = definition, PreferredWorkerId = WorkerId, Requester = WorkerId });
            return references;
        }

        /// <summary>
        /// Commit a group only when every member is ready. Each member must have its own preparation and admission check.
        /// <para>
        /// A member riding in another member's <see cref="DynamicContainer"/>, at any depth (the crew of a ship that is
        /// itself in the group), keeps its seat: it moves with its carrier and stays inside it, so it arrives in the
        /// destination scope through the carrier instead of being put down in the destination container. Its
        /// preparation is still what readies its owner's client for the destination.
        /// </para>
        /// </summary>
        /// <param name="transfers">Distinct entity preparations owned by this worker. This is not a cross-worker transaction.</param>
        /// <param name="translation">World-space displacement applied to each member, preserving its rotation.</param>
        public bool TryCommitTransfers(IReadOnlyList<InstanceTransfer> transfers, Vector3 translation)
        {
            if (transfers == null || transfers.Count == 0) return false;
            var entities = new HashSet<NetworkIdentity>();
            foreach (var transfer in transfers)
                if (transfer == null || !transfer.Ready || !ValidTransfer(transfer) || transfer.Entity.IsSceneEntity ||
                    !entities.Add(transfer.Entity) || !InstanceScenes.Prepare(transfer.Destination)) return false;
            var positions = new Vector3[transfers.Count];
            var rotations = new Quaternion[transfers.Count];
            var seated = new bool[transfers.Count];
            for (int i = 0; i < transfers.Count; i++)
            {
                positions[i] = transfers[i].Entity.transform.position + translation;
                rotations[i] = transfers[i].Entity.transform.rotation;
                seated[i] = RidesInAny(transfers[i].Entity, entities);
            }
            // Carriers first, then what they carry: a rider is committed in place once the ship it sits in has
            // already moved it (docs/scope-activation.md D19).
            for (int i = 0; i < transfers.Count; i++) if (!seated[i]) TryCommitTransfer(transfers[i], positions[i], rotations[i]);
            for (int i = 0; i < transfers.Count; i++) if (seated[i]) CommitSeated(transfers[i]);
            return true;
        }

        /// <summary>Whether <paramref name="entity"/> is carried, at any depth, by one of <paramref name="group"/>.</summary>
        private static bool RidesInAny(NetworkIdentity entity, HashSet<NetworkIdentity> group)
        {
            var container = entity.Container;
            // Bounded by the dynamic containers there are: a chain longer than that has revisited one, which is a loop
            // and not a seat.
            int limit = ContainerRegistry.Dynamic.Count;
            for (int hops = 0; container != null && container.IsDynamic && hops <= limit; hops++)
            {
                var carrier = container.Carrier;
                if (carrier == null || carrier == entity) return false;
                if (group.Contains(carrier)) return true;
                container = carrier.Container;
            }
            return false;
        }

        /// <summary>
        /// Commit a member that stays in its seat: its carrier has already crossed, so it is in the destination scope
        /// already. It keeps its container and pose; the crossing is recorded as it is for any other member — a new
        /// epoch, so every copy takes the next state as a fresh location, and the preparation is finished.
        /// </summary>
        private void CommitSeated(InstanceTransfer transfer)
        {
            if (!ValidTransfer(transfer)) return;
            var entity = transfer.Entity;
            entity.Epoch++;
            entity.HasStateTick = false;
            transfer.Finished = true;
            _instanceTransfers.Remove(transfer.Message.RequestId);
        }

        /// <summary>Prepare an authorized crossing. The game must check admission before calling. Preparation grants no visibility of destination entities.</summary>
        /// <param name="entity">Spawned entity simulated by this worker. Authored scene entities cannot commit a crossing.</param>
        /// <param name="destination">Static destination container with a current owning lease.</param>
        /// <param name="timeoutSeconds">Preparation timeout in unscaled seconds, clamped to at least one second.</param>
        public InstanceTransfer PrepareTransfer(NetworkIdentity entity, Container destination, float timeoutSeconds = 15)
        {
            if (entity == null || !entity.HasAuthority || destination == null || destination.IsDynamic)
                throw new ArgumentException("An authoritative entity and a static destination are required");
            var transfer = new InstanceTransfer { Entity = entity, Source = entity.Container, Epoch = entity.Epoch,
                Destination = destination, ClientReady = entity.OwnerClientId == 0, Deadline = Time.unscaledTime + Mathf.Max(1, timeoutSeconds) };
            if (!TransferScopeAdmits(destination))
            {
                transfer.Error = "Destination scope is not accepting transfers";
                transfer.Finished = true;
                return transfer;
            }
            // Capacity, before anything is sent: a crossing into a destination that is at capacity fails typed, with
            // the same reason and the same hook a join goes through, so a travel service gets one answer for both
            // ways in (docs/capacity-admission.md). Nothing is ever split silently to make room.
            var capacity = NebulaCapacity.Target(ControlPlane, destination.ScopeKey ?? "", destination.ContainerId);
            if (capacity.AtCapacity || NebulaAdmission.AlwaysConsult)
            {
                var decision = NebulaAdmission.Ask(new AdmissionRequest
                {
                    Kind = AdmissionKind.Transfer,
                    ScopeKey = destination.ScopeKey ?? "",
                    ContainerId = destination.ContainerId,
                    Capacity = capacity,
                    EntityNetId = entity.NetId,
                    OwnerClientId = entity.OwnerClientId,
                    ClientId = entity.OwnerClientId,
                }, $"entity {entity.NetId} crossing into {destination.ContainerId}");
                // A transfer has nobody to hold: there is no join to keep open, so "wait" is a refusal the caller
                // retries by preparing again.
                if (decision.Action != AdmissionAction.Admit)
                {
                    transfer.RejectReason = capacity.AtCapacity ? JoinRejectReason.AtCapacity : JoinRejectReason.Denied;
                    transfer.Saturation = capacity.Saturation;
                    transfer.Error = string.IsNullOrEmpty(decision.Reason) ? NebulaAdmission.DefaultReason(capacity) : decision.Reason;
                    transfer.Finished = true;
                    return transfer;
                }
            }
            transfer.Message = new InstancePreparationMsg { RequestId = ++_nextInstanceRequest, EntityId = entity.NetId,
                Destination = destination.Ref, SourceWorker = WorkerIndex, LeaseEpoch = destination.LeaseEpoch };
            _instanceTransfers.Add(transfer.Message.RequestId, transfer);
            if (destination.OwnerWorkerId == WorkerId)
                transfer.WorkerReady = InstanceScenes.Prepare(destination);
            else if (_workerPeersById.TryGetValue(destination.OwnerWorkerId, out var peer) && peer.HelloReceived)
            {
                _writer.Reset(); transfer.Message.Write(_writer, MsgId.InstancePrepare);
                _transport.Send(peer.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
            }
            if (!transfer.ClientReady)
            {
                // The owner's gateway is the one that has to get its client ready; the others do not know the
                // client and would only drop it. Broadcast only when the session's gateway cannot be resolved.
                _writer.Reset(); transfer.Message.Write(_writer, MsgId.InstancePrepare);
                var session = SessionGatewayOf(entity);
                if (session != null) _transport.Send(session.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
                else foreach (var gateway in _gateways) _transport.Send(gateway.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
            }
            return transfer;
        }

        /// <summary>Commit a prepared crossing at a world-space pose. Preserves identity and velocity. Returns false if readiness or authority changed.</summary>
        public bool TryCommitTransfer(InstanceTransfer transfer, Vector3 position, Quaternion rotation)
        {
            if (transfer == null || !transfer.Ready || !ValidTransfer(transfer)) return false;
            var entity = transfer.Entity;
            if (entity.IsSceneEntity) throw new InvalidOperationException("Scene entities cannot leave their authored scene");
            if (!InstanceScenes.Prepare(transfer.Destination)) return false;
            entity.SetContainer(transfer.Destination);
            entity.transform.SetPositionAndRotation(position, rotation);
            entity.Epoch++;
            entity.HasStateTick = false;
            transfer.Finished = true;
            _instanceTransfers.Remove(transfer.Message.RequestId);
            return true;
        }

        private bool ValidTransfer(InstanceTransfer transfer) => transfer.Entity != null && transfer.Entity.IsSpawned &&
            transfer.Entity.HasAuthority && transfer.Entity.Epoch == transfer.Epoch && transfer.Entity.Container == transfer.Source &&
            transfer.Destination != null && transfer.Destination.LeaseEpoch == transfer.Message.LeaseEpoch &&
            LeaseState.IsOwning(transfer.Destination.LeaseState) && TransferScopeAdmits(transfer.Destination);

        private bool TransferScopeAdmits(Container destination) =>
            ScopeLifecycle.Admits(ControlPlane, destination.ScopeKey, out _);

        private void UpdateInstancePreparations()
        {
            _expiredInstanceRequests.Clear();
            foreach (var pair in _instanceTransfers)
            {
                var transfer = pair.Value;
                if (Time.unscaledTime < transfer.Deadline && ValidTransfer(transfer)) continue;
                transfer.Error = "Preparation expired, container authority changed, or destination scope stopped admitting";
                transfer.Finished = true;
                _expiredInstanceRequests.Add(pair.Key);
            }
            foreach (var id in _expiredInstanceRequests) _instanceTransfers.Remove(id);
        }

        private void OnInstancePrepare(Peer peer, InstancePreparationMsg message)
        {
            if (peer.Role != PeerRole.Worker || peer.Index != message.SourceWorker) return;
            var destination = message.Destination.Resolve();
            message.Success = destination != null && destination.OwnerWorkerId == WorkerId &&
                destination.LeaseEpoch == message.LeaseEpoch && TransferScopeAdmits(destination) && InstanceScenes.Prepare(destination);
            _writer.Reset(); message.Write(_writer, MsgId.InstanceReady);
            _transport.Send(peer.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
        }

        private void OnInstanceReady(Peer peer, InstancePreparationMsg message)
        {
            if (!_instanceTransfers.TryGetValue(message.RequestId, out var transfer) || message.EntityId != transfer.Entity.NetId ||
                message.Destination != transfer.Message.Destination || message.LeaseEpoch != transfer.Message.LeaseEpoch) return;
            if (peer.Role == PeerRole.Worker && peer.Id != transfer.Destination.OwnerWorkerId) return;
            if (peer.Role != PeerRole.Worker && peer.Role != PeerRole.Gateway) return;
            if (!message.Success) { transfer.Error = "Destination content is unavailable"; return; }
            if (peer.Role == PeerRole.Gateway) transfer.ClientReady = true;
            else transfer.WorkerReady = true;
        }
    }
}
