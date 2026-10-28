using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The worker's side of portal adjacency (<c>docs/portals.md</c> D29, D30): telling the workers that own the far containers a link's band touches about
    /// the link, and learning of the links other workers drive whose far side is here. The ghost band reads the relation (through the rigs of
    /// <see cref="PortalPhysics"/>) to ghost a body in a band to the worker that owns the other side; a worker that only holds such a ghost poses a proxy of it
    /// and never a body.
    /// </summary>
    public sealed partial class NebulaWorker
    {
        /// <summary>A link another worker drives, as it last told this one.</summary>
        internal sealed class IncomingAdjacency
        {
            public PortalAdjacencyMsg Msg;
            public string From;
        }

        private sealed class SentAdjacency
        {
            public int Hash;
            public uint At;
            public PortalState State;
            public uint Generation;
            public uint Ownership;
            public readonly HashSet<string> To = new HashSet<string>();
        }

        /// <summary>The adjacency relation of this worker's links and the links it holds the far side of (<c>docs/portals.md</c> D30).</summary>
        internal readonly PortalAdjacency PortalAdjacencyTable = new PortalAdjacency();

        private readonly Dictionary<ulong, IncomingAdjacency> _adjacencyIn = new Dictionary<ulong, IncomingAdjacency>();
        private readonly Dictionary<ulong, SentAdjacency> _adjacencySent = new Dictionary<ulong, SentAdjacency>();
        private readonly HashSet<ulong> _adjacencySeen = new HashSet<ulong>();
        private readonly List<Container> _adjacencyContainers = new List<Container>();
        private readonly List<ContainerRef> _adjacencyFar = new List<ContainerRef>();
        private readonly List<ContainerRef> _adjacencyRows = new List<ContainerRef>();
        private readonly HashSet<string> _adjacencyRecipients = new HashSet<string>();
        private readonly List<ulong> _adjacencyGone = new List<ulong>();
        private readonly NetworkWriter _adjacencyWriter = new NetworkWriter();
        private static uint AdjacencyRefreshTicks => (uint)NetworkTime.SecondsToTicks(0.5);

        /// <summary>Links other workers drive whose far side this worker holds.</summary>
        internal IEnumerable<IncomingAdjacency> AdjacencyIn => _adjacencyIn.Values;

        /// <summary>
        /// Once a tick, after the links have been driven: for every link of the portals this worker owns, keep the workers that own the far containers its band
        /// touches told of it. It is told again when the link's state or opening changes, when a lease moves, and every half second; a link that went away is withdrawn.
        /// </summary>
        private void PublishAdjacency()
        {
            if (Portals.All.Count == 0 && _adjacencySent.Count == 0) return;
            _adjacencySeen.Clear();
            var all = Portals.All;
            for (int i = 0; i < all.Count; i++)
            {
                var portal = all[i];
                if (portal == null || portal.IsIgnored) continue;
                var near = portal.NearContainer;
                if (near == null || near.OwnerWorkerId != WorkerId) continue;
                foreach (var link in portal.LinkValues)
                {
                    if (link.FarScope == 0) continue;
                    _adjacencySeen.Add(link.Id);
                    PublishAdjacency(portal, link, near);
                }
            }
            _adjacencyGone.Clear();
            foreach (var id in _adjacencySent.Keys) if (!_adjacencySeen.Contains(id)) _adjacencyGone.Add(id);
            for (int i = 0; i < _adjacencyGone.Count; i++) WithdrawAdjacency(_adjacencyGone[i]);
            _adjacencyGone.Clear();
        }

        private void WithdrawAdjacency(ulong linkId)
        {
            if (_adjacencySent.TryGetValue(linkId, out var sent))
            {
                foreach (var worker in sent.To)
                {
                    if (!_workerPeersById.TryGetValue(worker, out var peer) || !peer.HelloReceived) continue;
                    _adjacencyWriter.Reset();
                    new PortalAdjacencyMsg { Removed = true, LinkId = linkId }.Write(_adjacencyWriter);
                    Send(peer, Delivery.ReliableOrdered, _adjacencyWriter);
                }
                _adjacencySent.Remove(linkId);
            }
            PortalAdjacencyTable.Remove(linkId);
        }

        private void PublishAdjacency(Portal portal, PortalLink link, Container near)
        {
            if (!_adjacencySent.TryGetValue(link.Id, out var sent)) _adjacencySent.Add(link.Id, sent = new SentAdjacency());
            uint ownership = ContainerRegistry.OwnershipVersion;
            bool due = sent.At == 0 || CurrentTick - sent.At >= AdjacencyRefreshTicks || sent.State != link.State || sent.Generation != link.Generation || sent.Ownership != ownership;
            if (!due) return;
            sent.At = CurrentTick == 0 ? 1u : CurrentTick;
            sent.State = link.State;
            sent.Generation = link.Generation;
            sent.Ownership = ownership;
            FarContainersOf(portal, link);
            PortalAdjacencyTable.Set(link.Id, ContainerRef.Of(near), _adjacencyFar);
            // The other containers of the far window: their workers are asked to prepare them and ghost nothing for them (D16, D35).
            _adjacencyRows.Clear();
            for (int i = 0; i < link.Rows.Count; i++)
            {
                var rowRef = ContainerRef.Of(link.Rows[i].Container);
                if (!_adjacencyFar.Contains(rowRef)) _adjacencyRows.Add(rowRef);
            }
            var message = new PortalAdjacencyMsg
            {
                LinkId = link.Id,
                Generation = link.Generation,
                State = link.State,
                FarScope = link.FarScope,
                Portal = portal.ToRecord(),
                NearContainer = ContainerRef.Of(near),
                FarContainers = new List<ContainerRef>(_adjacencyFar),
                FarRows = new List<ContainerRef>(_adjacencyRows),
            };
            // The link's far face is what the far worker poses by: a fixed far side's own, a template's the near face's origin in the instance's own space.
            message.Portal.FarScope = link.FarScope;
            message.Portal.FarOrigin = link.FarFace.Origin;
            message.Portal.FarRotation = link.FarFace.Rotation;
            _adjacencyWriter.Reset();
            message.Write(_adjacencyWriter);
            int hash = HashBytes(_adjacencyWriter.ToSegment());
            _adjacencyRecipients.Clear();
            for (int i = 0; i < _adjacencyFar.Count; i++)
            {
                var container = ContainerRegistry.Resolve(_adjacencyFar[i]);
                string owner = container != null ? container.OwnerWorkerId : "";
                if (!string.IsNullOrEmpty(owner) && owner != WorkerId) _adjacencyRecipients.Add(owner);
            }
            for (int i = 0; i < _adjacencyRows.Count; i++)
            {
                var container = ContainerRegistry.Resolve(_adjacencyRows[i]);
                string owner = container != null ? container.OwnerWorkerId : "";
                if (!string.IsNullOrEmpty(owner) && owner != WorkerId) _adjacencyRecipients.Add(owner);
            }
            // Workers that no longer hold any of it are told it is gone; the others are told what changed, and any worker new to it everything.
            List<string> dropped = null;
            foreach (var worker in sent.To) if (!_adjacencyRecipients.Contains(worker)) (dropped ??= new List<string>()).Add(worker);
            if (dropped != null)
                foreach (var worker in dropped)
                {
                    sent.To.Remove(worker);
                    if (!_workerPeersById.TryGetValue(worker, out var peer) || !peer.HelloReceived) continue;
                    _adjacencyWriter.Reset();
                    new PortalAdjacencyMsg { Removed = true, LinkId = link.Id }.Write(_adjacencyWriter);
                    Send(peer, Delivery.ReliableOrdered, _adjacencyWriter);
                }
            bool changed = hash != sent.Hash;
            sent.Hash = hash;
            foreach (var worker in _adjacencyRecipients)
            {
                bool isNew = sent.To.Add(worker);
                if (!changed && !isNew) continue;
                if (!_workerPeersById.TryGetValue(worker, out var peer) || !peer.HelloReceived) { sent.To.Remove(worker); continue; }
                _adjacencyWriter.Reset();
                message.Write(_adjacencyWriter);
                Send(peer, Delivery.ReliableOrdered, _adjacencyWriter);
            }
        }

        /// <summary>
        /// Send the workers that own the far containers a link's band touches what they need before they are asked to prepare one (the preparation follows on the
        /// same ordered stream, so the adjacency is there when it arrives).
        /// </summary>
        internal void PublishAdjacencyNow(Portal portal, PortalLink link)
        {
            var near = portal.NearContainer;
            if (near == null || near.OwnerWorkerId != WorkerId || link.FarScope == 0) return;
            if (_adjacencySent.TryGetValue(link.Id, out var sent)) sent.At = 0;
            PublishAdjacency(portal, link, near);
        }

        private static int HashBytes(ArraySegment<byte> segment)
        {
            unchecked
            {
                int hash = (int)2166136261;
                for (int i = 0; i < segment.Count; i++) hash = (hash ^ segment.Array[segment.Offset + i]) * 16777619;
                return hash;
            }
        }

        /// <summary>The far entry, then every other container of the far scope whose box the far band touches, into <see cref="_adjacencyFar"/>.</summary>
        private void FarContainersOf(Portal portal, PortalLink link)
        {
            _adjacencyFar.Clear();
            if (link.FarEntry != null) _adjacencyFar.Add(ContainerRef.Of(link.FarEntry));
            var face = link.FarFace;
            var origin = ContainerRegistry.ToFrame(face.Origin, face.Scope);
            var band = new PortalBand(portal.Aperture, portal.BandMargin, Portal.BandDepth);
            var bounds = new Bounds(origin, Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) != 0 ? band.HalfWidth : -band.HalfWidth, (i & 2) != 0 ? band.HalfHeight : -band.HalfHeight, (i & 4) != 0 ? band.Depth : -band.Depth);
                bounds.Encapsulate(origin + face.Rotation * corner);
            }
            ContainerRegistry.Overlapping(bounds, _adjacencyContainers, link.FarScope);
            for (int i = 0; i < _adjacencyContainers.Count; i++)
            {
                var r = ContainerRef.Of(_adjacencyContainers[i]);
                if (!_adjacencyFar.Contains(r)) _adjacencyFar.Add(r);
            }
            _adjacencyContainers.Clear();
        }

        /// <summary>A worker tells this one about a link it drives whose far side this worker holds (<c>docs/portals.md</c> D30, D45).</summary>
        private void OnPortalAdjacency(Peer peer, PortalAdjacencyMsg message)
        {
            if (peer.Role != PeerRole.Worker) return;
            if (message.Removed)
            {
                if (_adjacencyIn.TryGetValue(message.LinkId, out var gone) && gone.From == peer.Id)
                {
                    _adjacencyIn.Remove(message.LinkId);
                    PortalAdjacencyTable.Remove(message.LinkId);
                }
                return;
            }
            _adjacencyIn[message.LinkId] = new IncomingAdjacency { Msg = message, From = peer.Id };
            PortalAdjacencyTable.Set(message.LinkId, message.NearContainer, message.FarContainers, peer.Id);
        }

        private readonly List<string> _portalTargets = new List<string>();
        private readonly Dictionary<ulong, List<Collider>> _portalGhostColliders = new Dictionary<ulong, List<Collider>>();

        /// <summary>
        /// Ghost an entity in the band of a portal face to the workers that own the other side (<c>docs/portals.md</c> D29, D30): its extent or its root is in
        /// the band, never merely because two scopes' boxes overlap.
        /// </summary>
        private void GhostAcrossPortals(NetworkIdentity e, ref Dictionary<string, float> targets, float now)
        {
            _portalTargets.Clear();
            _portalPhysics.GhostTargets(e, _portalTargets);
            for (int i = 0; i < _portalTargets.Count; i++) Ghost(e, _portalTargets[i], ref targets, now, viaPortal: true);
        }

        /// <summary>A ghost that arrived across a portal: its colliders go off, so it is no body here, and it is listed for the proxies of <see cref="PortalPhysics"/>.</summary>
        private void HoldAsPortalProxy(NetworkIdentity ghost)
        {
            ghost.IsPortalGhost = true;
            var disabled = new List<Collider>(2);
            foreach (var c in ghost.GetComponentsInChildren<Collider>(true))
            {
                if (!c.enabled) continue;
                c.enabled = false;
                disabled.Add(c);
            }
            _portalGhostColliders[ghost.NetId] = disabled;
            if (!_portalGhosts.Contains(ghost)) _portalGhosts.Add(ghost);
        }

        /// <summary>The entity is this worker's own now (a handover): its colliders come back.</summary>
        private void ReleaseFromPortalProxy(NetworkIdentity e)
        {
            e.IsPortalGhost = false;
            _portalGhosts.Remove(e);
            _portalPhysics?.Untrack(e);
            if (!_portalGhostColliders.TryGetValue(e.NetId, out var disabled)) return;
            _portalGhostColliders.Remove(e.NetId);
            foreach (var c in disabled) if (c != null) c.enabled = true;
        }

        /// <summary>
        /// The far worker's answer to a link's preparation (<c>docs/portals.md</c> D10 condition 4): it has been told of the link, and has built its side's aperture
        /// collider. False when it was not told, which fails the preparation; the near worker asks again.
        /// </summary>
        private bool BuildFarSideOf(ulong linkId) =>
            !Nebula.PortalPhysics.Enabled || (_adjacencyIn.ContainsKey(linkId) && PortalPhysics.PrepareFarSide(linkId, CurrentTick));
    }
}
