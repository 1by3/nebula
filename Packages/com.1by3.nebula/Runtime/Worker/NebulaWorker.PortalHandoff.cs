using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// <c>docs/portals.md</c> D44: what happens to a portal's links when the container its near face is fixed in is reassigned on purpose. The worker that
    /// is releasing the container describes the links to the worker that takes it (<see cref="MsgId.PortalHandoff"/>) in the same tick it notices the
    /// release, and lets its own copies go; the new owner installs them waiting on <see cref="PortalWait.NearContainerMoving"/>, looks at everything again and
    /// opens them. A crash sends nothing: the links are lost and read closed, which is the safe state.
    /// </summary>
    public sealed partial class NebulaWorker
    {
        /// <summary>How long a handoff waits for this worker to hold the portal's content and the lease of its container before it is dropped.</summary>
        private const float PendingHandoffSeconds = 15f;

        /// <summary>How long the network gateways keep the records of links this worker handed away before they are told they are gone (the new owner has replaced them by then).</summary>
        private const float DeferredRemovalSeconds = 1f;

        private sealed class PendingHandoff
        {
            public PortalHandoffMsg Msg;
            public float Deadline;
        }

        private struct DeferredRemoval
        {
            public ulong PortalId;
            public ulong[] Links;
            public float Due;
        }

        private readonly List<PendingHandoff> _pendingHandoffs = new List<PendingHandoff>();
        private readonly List<DeferredRemoval> _deferredRemovals = new List<DeferredRemoval>();
        private readonly List<PortalHandoffLink> _handoffScratch = new List<PortalHandoffLink>();

        /// <summary>
        /// The near container of <paramref name="portal"/> now belongs to another worker: send it the portal's links and let this worker's copies go. False when the
        /// container has no owner yet or its owner is not connected, in which case the caller closes the links.
        /// </summary>
        private bool HandOffLinks(Portal portal, Container near)
        {
            string owner = near.OwnerWorkerId;
            if (string.IsNullOrEmpty(owner) || owner == WorkerId) return false;
            if (!_workerPeersById.TryGetValue(owner, out var peer) || !peer.HelloReceived) return false;
            _handoffScratch.Clear();
            ulong[] linkIds = null;
            if (_publishedPortals.TryGetValue(portal.PortalId, out var published))
            {
                linkIds = new ulong[published.Links.Count];
                published.Links.CopyTo(linkIds);
            }
            portal.ReleaseLinks(_handoffScratch);
            var message = new PortalHandoffMsg { PortalId = portal.PortalId, NearContainerId = near.ContainerId, Links = new List<PortalHandoffLink>(_handoffScratch) };
            _handoffScratch.Clear();
            _writer.Reset();
            message.Write(_writer);
            _transport.Send(peer.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
            NebulaDiagnostics.PortalHandoffs++;
            // The network gateways keep this worker's records until the new owner has replaced them (a record is replaced by its owner's, and a removal from a worker
            // that no longer owns it is ignored), so a link the new owner opens again within a moment never flickers for a client.
            _publishedPortals.Remove(portal.PortalId);
            _deferredRemovals.Add(new DeferredRemoval { PortalId = portal.PortalId, Links = linkIds ?? Array.Empty<ulong>(), Due = Time.unscaledTime + DeferredRemovalSeconds });
            return true;
        }

        private void CollectDeferredRemovals()
        {
            float now = Time.unscaledTime;
            for (int i = _deferredRemovals.Count - 1; i >= 0; i--)
            {
                var removal = _deferredRemovals[i];
                if (now < removal.Due) continue;
                _deferredRemovals.RemoveAt(i);
                // Withdrawn only if the portal is not one this worker drives again.
                if (_publishedPortals.ContainsKey(removal.PortalId)) continue;
                _portalRemoves.Add(removal.PortalId);
                for (int l = 0; l < removal.Links.Length; l++) _linkRemoves.Add(removal.Links[l]);
            }
        }

        /// <summary>The worker that released a portal's near container sent its links (<c>docs/portals.md</c> D44).</summary>
        private void OnPortalHandoff(Peer peer, PortalHandoffMsg message)
        {
            if (peer.Role != PeerRole.Worker) return;
            if (!TryInstallHandoff(message)) _pendingHandoffs.Add(new PendingHandoff { Msg = message, Deadline = Time.unscaledTime + PendingHandoffSeconds });
        }

        /// <summary>Install a handoff once this worker holds the portal and owns its container; false while it does not.</summary>
        private bool TryInstallHandoff(in PortalHandoffMsg message)
        {
            var portal = Portals.Find(message.PortalId);
            if (portal == null) return false;
            var near = portal.NearContainer;
            if (near == null || near.OwnerWorkerId != WorkerId) return false;
            portal.AcceptHandoff(message.Links);
            return true;
        }

        private void ApplyPendingHandoffs()
        {
            float now = Time.unscaledTime;
            for (int i = _pendingHandoffs.Count - 1; i >= 0; i--)
            {
                var pending = _pendingHandoffs[i];
                if (TryInstallHandoff(pending.Msg)) { _pendingHandoffs.RemoveAt(i); continue; }
                if (now < pending.Deadline) continue;
                NebulaLog.Warn($"the links of portal {pending.Msg.PortalId:x} in {pending.Msg.NearContainerId} were handed to this worker, but it never held the portal and the lease of its container; they are dropped and the portal reads closed");
                _pendingHandoffs.RemoveAt(i);
            }
        }
    }
}
