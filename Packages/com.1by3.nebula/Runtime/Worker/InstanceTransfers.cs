using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
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
        public static ulong InstanceKey(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("An instance key is required", nameof(key));
            using (var hash = SHA256.Create())
            {
                var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(key));
                ulong id = 0;
                for (int i = 0; i < 8; i++) id = (id << 8) | bytes[i];
                return id == 0 ? 1UL : id;
            }
        }

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
            for (int i = 0; i < template.Parts.Length; i++)
            {
                var part = template.Parts[i];
                ulong id = InstanceKey(prefix + "/" + part.Id);
                references[i] = ContainerRef.Runtime(id);
                var bounds = ContainerRegistry.ToAbsolute(new Bounds(origin + part.Bounds.center, part.Bounds.size));
                var view = ContainerRegistry.ToAbsolute(new Bounds(origin + template.PublicView.center, template.PublicView.size));
                var info = new InstanceContainerInfo { InstanceId = scope, ContentResource = part.ContentResource,
                    ObservePublic = template.ObservePublic, ObservationCenter = view.center, ObservationSize = view.size };
                var existing = ControlPlane.FindLease(ContainerRegistry.RuntimeContainerId(id));
                if (existing != null && (existing.Instance?.InstanceId != scope || existing.Bounds != bounds || existing.Instance.ContentResource != part.ContentResource))
                    throw new InvalidOperationException("Instance key already names different content or bounds");
                if (existing == null) ControlPlane.EnsureRuntimeContainer(ContainerRegistry.RuntimeContainerId(id), bounds, WorkerId, info);
            }
            return references;
        }

        /// <summary>Commit a group only when every member is ready. Each member must have its own preparation and admission check.</summary>
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
            for (int i = 0; i < transfers.Count; i++)
            {
                positions[i] = transfers[i].Entity.transform.position + translation;
                rotations[i] = transfers[i].Entity.transform.rotation;
            }
            for (int i = 0; i < transfers.Count; i++) TryCommitTransfer(transfers[i], positions[i], rotations[i]);
            return true;
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
            LeaseState.IsOwning(transfer.Destination.LeaseState);

        private void UpdateInstancePreparations()
        {
            _expiredInstanceRequests.Clear();
            foreach (var pair in _instanceTransfers)
            {
                var transfer = pair.Value;
                if (Time.unscaledTime < transfer.Deadline && ValidTransfer(transfer)) continue;
                transfer.Error = "Preparation expired or container authority changed";
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
                destination.LeaseEpoch == message.LeaseEpoch && InstanceScenes.Prepare(destination);
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
