using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// The worker's side of portals (<c>docs/portals.md</c>): driving the links of the portals whose container it owns, publishing
    /// them to the network gateways, taking a member client's <see cref="MsgId.PortalWarm"/>, asking another worker for a far
    /// container, and committing a crossing through an open link.
    /// </summary>
    public sealed partial class NebulaWorker
    {
        private sealed class PublishedPortal
        {
            public Portal Portal;
            public int RecordHash;
            public bool RecordSent;
            public readonly HashSet<ulong> Links = new HashSet<ulong>();
        }

        private readonly Dictionary<ulong, PublishedPortal> _publishedPortals = new Dictionary<ulong, PublishedPortal>();
        private readonly HashSet<ulong> _portalsSeen = new HashSet<ulong>();
        private readonly List<ulong> _portalGone = new List<ulong>();
        private readonly List<Portal> _portalScratch = new List<Portal>();
        private readonly List<PortalRecord> _portalUpserts = new List<PortalRecord>();
        private readonly List<ulong> _portalRemoves = new List<ulong>();
        private readonly List<PortalLinkRecord> _linkUpserts = new List<PortalLinkRecord>();
        private readonly List<ulong> _linkRemoves = new List<ulong>();
        private readonly NetworkWriter _portalWriter = new NetworkWriter();

        /// <summary>The entities this worker simulates, for the portals' band test.</summary>
        internal IReadOnlyList<NetworkIdentity> AuthoritativeEntities => _authoritative;

        private PortalPhysics _portalPhysics;

        /// <summary>This worker's portal physics: the aperture colliders, the throat and the proxies it builds in its scenes (<c>docs/portals.md</c> §6).</summary>
        internal PortalPhysics PortalPhysics => _portalPhysics ??= new PortalPhysics(this);
        internal PortalPhysics PortalPhysicsIfAny => _portalPhysics;

        /// <summary>Ghosts this worker holds only to pose a proxy of them through a portal, never as bodies in their own scope (<c>docs/portals.md</c> D29).</summary>
        internal IReadOnlyList<NetworkIdentity> PortalGhostEntities => _portalGhosts;
        private readonly List<NetworkIdentity> _portalGhosts = new List<NetworkIdentity>();

        /// <summary>
        /// Once a tick, after the preparations: drive every link of the portals this worker owns, and tell the network gateways what
        /// changed. A portal whose container this worker no longer owns, or that is gone, is withdrawn.
        /// </summary>
        private void UpdatePortals()
        {
            var all = Portals.All;
            if (all.Count == 0 && _publishedPortals.Count == 0)
            {
                NebulaDiagnostics.OpenPortalLinks = NebulaDiagnostics.SplitPortalLinks = 0;
                return;
            }
            _portalsSeen.Clear();
            _portalUpserts.Clear(); _portalRemoves.Clear(); _linkUpserts.Clear(); _linkRemoves.Clear();
            _portalScratch.Clear();
            for (int i = 0; i < all.Count; i++) _portalScratch.Add(all[i]);
            int open = 0, split = 0;
            for (int i = 0; i < _portalScratch.Count; i++)
            {
                var portal = _portalScratch[i];
                if (portal == null) continue;
                var near = portal.NearContainer;
                if (near == null || near.OwnerWorkerId != WorkerId)
                {
                    // The container moved to another worker (or is not known): the links this worker held read Closed (D44).
                    if (portal.LinkCount > 0 && _publishedPortals.ContainsKey(portal.PortalId)) portal.CloseForReassignment();
                    continue;
                }
                ulong id = portal.PortalId;
                if (!_portalsSeen.Add(id)) { portal.MarkDuplicate(); continue; }
                portal.ClearDuplicate();
                portal.Drive(this);

                if (!_publishedPortals.TryGetValue(id, out var published)) _publishedPortals.Add(id, published = new PublishedPortal { Portal = portal });
                published.Portal = portal;
                var record = portal.ToRecord();
                int hash = HashOf(record);
                if (hash != published.RecordHash || !published.RecordSent)
                {
                    published.RecordHash = hash;
                    published.RecordSent = true;
                    _portalUpserts.Add(record);
                }
                foreach (var link in portal.Links)
                {
                    if (link.State == PortalState.Open)
                    {
                        open++;
                        if (link.FarEntry != null && !string.IsNullOrEmpty(link.FarEntry.OwnerWorkerId) && link.FarEntry.OwnerWorkerId != WorkerId) split++;
                    }
                    if (!link.Dirty && link.Published) continue;
                    link.Dirty = false;
                    link.Published = true;
                    published.Links.Add(link.Id);
                    _linkUpserts.Add(Portal.ToLinkRecord(link));
                }
            }
            _portalGone.Clear();
            foreach (var kv in _publishedPortals)
                if (!_portalsSeen.Contains(kv.Key)) _portalGone.Add(kv.Key);
            for (int i = 0; i < _portalGone.Count; i++)
            {
                var published = _publishedPortals[_portalGone[i]];
                _publishedPortals.Remove(_portalGone[i]);
                _portalRemoves.Add(_portalGone[i]);
                foreach (ulong linkId in published.Links) _linkRemoves.Add(linkId);
            }
            _portalGone.Clear();
            _portalScratch.Clear();
            PublishAdjacency();
            NebulaDiagnostics.OpenPortalLinks = open;
            NebulaDiagnostics.SplitPortalLinks = split;
            if (_portalUpserts.Count + _portalRemoves.Count + _linkUpserts.Count + _linkRemoves.Count == 0 || _gateways.Count == 0)
            {
                // Nothing to say, or nobody to say it to: a gateway that connects later is sent everything (PublishPortalsTo).
                return;
            }
            SendPortals(_portalUpserts, _portalRemoves, _linkUpserts, _linkRemoves, null);
        }

        private readonly NetworkWriter _hashWriter = new NetworkWriter();

        private int HashOf(in PortalRecord record)
        {
            _hashWriter.Reset();
            record.Write(_hashWriter);
            var segment = _hashWriter.ToSegment();
            unchecked
            {
                int hash = (int)2166136261;
                for (int i = 0; i < segment.Count; i++) hash = (hash ^ segment.Array[segment.Offset + i]) * 16777619;
                return hash;
            }
        }

        /// <summary>Tell the gateways (or just <paramref name="only"/>) about portals and links, the portals first so a link never arrives before the portal it belongs to.</summary>
        private void SendPortals(List<PortalRecord> portals, List<ulong> portalRemoves, List<PortalLinkRecord> links, List<ulong> linkRemoves, Peer only)
        {
            if (portals.Count > 0 || portalRemoves.Count > 0)
            {
                _portalWriter.Reset();
                PortalsMsg.Write(_portalWriter, portals, portalRemoves);
                SendToGateways(only);
            }
            if (links.Count > 0 || linkRemoves.Count > 0)
            {
                _portalWriter.Reset();
                PortalLinksMsg.Write(_portalWriter, links, linkRemoves);
                SendToGateways(only);
            }
        }

        private void SendToGateways(Peer only)
        {
            if (only != null) { Send(only, Delivery.ReliableOrdered, _portalWriter); return; }
            for (int g = 0; g < _gateways.Count; g++) Send(_gateways[g], Delivery.ReliableOrdered, _portalWriter);
        }

        /// <summary>A gateway just connected: send it every portal this worker publishes and the state of every link.</summary>
        private void PublishPortalsTo(Peer gateway)
        {
            if (_publishedPortals.Count == 0) return;
            var portals = new List<PortalRecord>();
            var links = new List<PortalLinkRecord>();
            foreach (var published in _publishedPortals.Values)
            {
                if (published.Portal == null) continue;
                portals.Add(published.Portal.ToRecord());
                foreach (var link in published.Portal.Links) if (link.Published) links.Add(Portal.ToLinkRecord(link));
            }
            SendPortals(portals, new List<ulong>(), links, new List<ulong>(), gateway);
        }

        /// <summary>A member client says it holds a link's far window and its content is ready (<c>docs/portals.md</c> D10, D45).</summary>
        private void OnPortalWarm(Peer peer, PortalWarmMsg message)
        {
            if (peer.Role != PeerRole.Gateway) return;
            var link = Portals.FindLink(message.LinkId);
            if (link == null || message.Generation != link.Generation || !link.IsMember(message.ClientId)) return;
            var near = link.Portal.NearContainer;
            if (near == null || near.OwnerWorkerId != WorkerId) return;
            if (link.WarmSet.Add(message.ClientId)) link.Dirty = true;
        }

        /// <summary>
        /// Ask the worker that owns a link's far container to prepare it (<c>docs/portals.md</c> D35): the exchange that readies one entity's
        /// crossing, carrying the link's id instead of an entity's. Asked again when it goes unanswered for a while.
        /// </summary>
        internal void AskFarWorker(PortalLink link, Container entry)
        {
            float now = Time.unscaledTime;
            if (link.HandshakeSent && now - link.HandshakeAt < 5f) return;
            if (!_workerPeersById.TryGetValue(entry.OwnerWorkerId, out var peer) || !peer.HelloReceived) return;
            link.HandshakeSent = true;
            link.HandshakeAt = now;
            var message = new InstancePreparationMsg
            {
                RequestId = ++_nextInstanceRequest, EntityId = 0, Destination = entry.Ref,
                SourceWorker = WorkerIndex, LeaseEpoch = entry.LeaseEpoch, LinkId = link.Id,
            };
            link.HandshakeRequest = message.RequestId;
            _writer.Reset();
            message.Write(_writer, MsgId.InstancePrepare);
            _transport.Send(peer.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
        }

        /// <summary>The far worker answered a link's preparation (<see cref="AskFarWorker"/>).</summary>
        private void OnLinkReady(Peer peer, InstancePreparationMsg message)
        {
            if (peer.Role != PeerRole.Worker) return;
            var link = Portals.FindLink(message.LinkId);
            if (link == null || link.HandshakeRequest != message.RequestId || link.FarEntry == null || link.FarEntry.OwnerWorkerId != peer.Id) return;
            if (message.LeaseEpoch != link.FarEntry.LeaseEpoch) return;
            if (!message.Success)
            {
                link.Error = "The far worker could not prepare the far container";
                link.HandshakeSent = false;
                return;
            }
            link.HandshakeDone = true;
            link.Dirty = true;
        }

        /// <summary>
        /// Commit a crossing through an open link (<c>docs/portals.md</c> D16, D18): no per-entity preparation, nothing loaded and nothing held.
        /// Every member is put into <paramref name="destination"/> at its own pose carried through the link, in double, keeping its identity; a
        /// member riding in another member's carried container, at any depth, keeps its seat. Its velocities (the reported one and a dynamic
        /// body's linear and angular), its recorded history and its epoch go through the link with it. Fails, changing nothing, when an entity
        /// is not simulated here or cannot leave its scene, the destination's lease or scope no longer admits it, or its content cannot be completed.
        /// </summary>
        internal bool CommitThrough(IReadOnlyList<NetworkIdentity> members, Container destination, in PortalMap map)
        {
            if (members == null || members.Count == 0 || destination == null || !LeaseState.IsOwning(destination.LeaseState) || !TransferScopeAdmits(destination)) return false;
            var group = new HashSet<NetworkIdentity>();
            foreach (var member in members)
                if (member == null || !member.IsSpawned || !member.HasAuthority || member.IsSceneEntity || !group.Add(member)) return false;
            if (!InstanceScenes.Complete(destination)) return false;
            // Their proxies in the scene they are about to enter go before they arrive in it; the ones in the scene they leave are built on the same tick, below.
            _portalPhysics?.BeforeCommit(members);
            // Everything is read in the scope being left before anything moves: a rider's frame, and so its velocity, changes when its carrier does.
            var carries = new NetworkIdentity.PortalCarry[members.Count];
            var seated = new bool[members.Count];
            for (int i = 0; i < members.Count; i++)
            {
                carries[i] = members[i].CapturePortalState();
                seated[i] = RidesInAny(members[i], group);
            }
            // Carriers first, then what they carry: a rider is committed in place once the ship it sits in has already moved it
            // (docs/scope-activation.md D19).
            for (int i = 0; i < members.Count; i++) if (!seated[i]) members[i].MoveThroughPortal(destination, in map, in carries[i]);
            for (int i = 0; i < members.Count; i++) members[i].FinishThroughPortal(in map, in carries[i]);
            _portalPhysics?.AfterCommit(members);
            return true;
        }
    }
}
