using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// A client's half of portal readiness (<c>docs/portals.md</c> D10, D45). The network gateway tells a client which portal links it is a
    /// member of, with the container row of the far window ahead of the link. A member prepares that container, and once its content is
    /// ready says so with <see cref="MsgId.PortalWarm"/>; the worker opens the link when every member has. Nothing here draws or simulates the
    /// far scope: the replica handling is the ordinary one.
    /// </summary>
    public sealed partial class NebulaClient
    {
        private sealed class WarmingLink
        {
            public ulong LinkId;
            public uint Generation;
            public ContainerRef FarEntry;
            public System.Collections.Generic.List<ContainerRef> Rows;
            public int Peer;
            public bool Sent;
            public bool Warned;
        }

        private readonly Dictionary<ulong, WarmingLink> _warming = new Dictionary<ulong, WarmingLink>();
        private readonly List<ulong> _warmScratch = new List<ulong>();

        /// <summary>How many links this client is a member of and has not yet told their worker it holds the far window of.</summary>
        public int LinksWarming
        {
            get
            {
                int n = 0;
                foreach (var link in _warming.Values) if (!link.Sent) n++;
                return n;
            }
        }

        private void OnPortalLinks(PortalLinksUpdate update)
        {
            Portals.ApplyLinks(update);
            foreach (var record in update.Upserts)
            {
                bool wanted = (record.Flags & PortalLinkFlags.Member) != 0 && record.State != PortalState.Closed;
                if (!wanted) { _warming.Remove(record.LinkId); continue; }
                // A new opening of the link (its generation changed) is warmed again.
                if (_warming.TryGetValue(record.LinkId, out var current) && current.Generation == record.Generation && current.Peer == _gatewayPeer)
                {
                    current.FarEntry = record.FarEntry;
                    current.Rows = record.FarRows;
                    continue;
                }
                _warming[record.LinkId] = new WarmingLink { LinkId = record.LinkId, Generation = record.Generation, FarEntry = record.FarEntry, Rows = record.FarRows, Peer = _gatewayPeer };
            }
            foreach (ulong id in update.Removes) _warming.Remove(id);
        }

        /// <summary>
        /// Prepare the far container of each link this client is a member of, and say so when its content is ready. Once a frame, and only while a
        /// link waits. A link asked on a connection the client has since left is dropped: its worker will ask again.
        /// </summary>
        internal void TickPortalWarm()
        {
            if (_warming.Count == 0) return;
            _warmScratch.Clear();
            foreach (var kv in _warming) _warmScratch.Add(kv.Key);
            for (int i = 0; i < _warmScratch.Count; i++)
            {
                var link = _warming[_warmScratch[i]];
                if (link.Peer != _gatewayPeer || _gatewayPeer < 0) { _warming.Remove(link.LinkId); continue; }
                if (link.Sent) continue;
                // Every container of the far window the worker named (the entry alone for a link that has not named them), each prepared and its content ready (D10, D16).
                bool ready = true;
                int rows = link.Rows != null && link.Rows.Count > 0 ? link.Rows.Count : 1;
                for (int r = 0; r < rows && ready; r++)
                {
                    var destination = (link.Rows != null && link.Rows.Count > 0 ? link.Rows[r] : link.FarEntry).Resolve();
                    if (destination == null) { ready = false; break; } // its row is on its way: the gateway sends it ahead of the link
                    if (!InstanceScenes.Prepare(destination))
                    {
                        if (!link.Warned) { link.Warned = true; NebulaLog.Warn($"portal link {link.LinkId:x}: the content of {destination.ContainerId} cannot be prepared"); }
                        ready = false;
                        break;
                    }
                    var content = InstanceScenes.ContentState(destination);
                    if (content == InstanceContentState.Loading) { ready = false; break; }
                    if (content == InstanceContentState.Failed)
                    {
                        if (!link.Warned) { link.Warned = true; NebulaLog.Warn($"portal link {link.LinkId:x}: the content of {destination.ContainerId} failed to load"); }
                        ready = false;
                        break;
                    }
                }
                if (!ready) continue;
                link.Sent = true;
                _writer.Reset();
                new PortalWarmMsg { LinkId = link.LinkId, Generation = link.Generation }.Write(_writer);
                _transport.Send(_gatewayPeer, Delivery.ReliableOrdered, _writer.ToSegment());
            }
            _warmScratch.Clear();
        }
    }
}
