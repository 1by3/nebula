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
    /// Portals, seen from the network gateway (<c>docs/portals.md</c> D11, D21, D45). A worker tells the gateway about the portals
    /// whose container it owns and the state of each party's link (<see cref="MsgId.Portals"/>, <see cref="MsgId.PortalLinks"/>); the gateway
    /// keeps them as records, with each link's members and observers, and never runs game code. A client is sent a portal's record and
    /// its links on the reliable stream behind the container row of either of its faces, and the records are withdrawn with the row. A link
    /// carries to a client only whether that client is a member (it must hold the far window and acknowledge it) and whether it is an
    /// observer. The far container's row is pinned for a member while its link is live, so the client can prepare it before the link
    /// opens; its <see cref="MsgId.PortalWarm"/> goes back to the worker that owns the portal.
    /// </summary>
    public sealed partial class NebulaGateway
    {
        private sealed class PortalEntry
        {
            public PortalRecord Record;
            public ushort Worker;
            public int Version;
            public readonly List<LinkEntry> Links = new List<LinkEntry>();
        }

        private sealed class LinkEntry
        {
            public PortalLinkRecord Record;
            public ushort Worker;
            public int Version;
            public readonly HashSet<ulong> Members = new HashSet<ulong>();
            public readonly HashSet<ulong> Observers = new HashSet<ulong>();
        }

        private readonly Dictionary<ulong, PortalEntry> _portals = new Dictionary<ulong, PortalEntry>();
        private readonly Dictionary<ulong, LinkEntry> _portalLinks = new Dictionary<ulong, LinkEntry>();
        /// <summary>The live links each client is a member of, by client id: the far rows the gateway keeps pinned for it.</summary>
        private readonly Dictionary<ulong, HashSet<ulong>> _memberLinks = new Dictionary<ulong, HashSet<ulong>>();
        private int _portalVersion;
        private readonly List<PortalRecord> _portalUpsertScratch = new List<PortalRecord>();
        private readonly List<ulong> _portalRemoveScratch = new List<ulong>();
        private readonly List<PortalLinkRecord> _linkUpsertScratch = new List<PortalLinkRecord>();
        private readonly List<ulong> _linkRemoveScratch = new List<ulong>();
        private readonly List<ulong> _portalIdScratch = new List<ulong>();
        private readonly NetworkWriter _portalWriter = new NetworkWriter();

        /// <summary>How many portals this gateway holds a record of (<c>docs/portals.md</c> D45).</summary>
        public int PortalCount => _portals.Count;
        /// <summary>How many portal links this gateway holds a record of.</summary>
        public int PortalLinkCount => _portalLinks.Count;

        /// <summary>The state this gateway last heard of a link, or false when it has no record of it.</summary>
        public bool TryGetPortalLinkState(ulong linkId, out PortalState state, out PortalWait wait)
        {
            if (_portalLinks.TryGetValue(linkId, out var link)) { state = link.Record.State; wait = link.Record.Wait; return true; }
            state = PortalState.Closed;
            wait = PortalWait.None;
            return false;
        }

        /// <summary>
        /// Whether a client may look through a link, from the observer list its worker published (<c>docs/portals.md</c> D21): the rule the
        /// gateway enforces without running game code. False for a link it has no record of.
        /// </summary>
        public bool PortalObserves(ulong clientId, ulong linkId) =>
            _portalLinks.TryGetValue(linkId, out var link) && (link.Observers.Contains(clientId) || (link.Record.Flags & PortalLinkFlags.ObserverAnyone) != 0);

        // ------------------------------------------------------------------------------------------- from workers

        private void OnPortals(WorkerConn w, PortalsUpdate update)
        {
            foreach (var record in update.Upserts)
            {
                if (!_portals.TryGetValue(record.PortalId, out var entry)) _portals.Add(record.PortalId, entry = new PortalEntry());
                entry.Record = record;
                entry.Worker = w.Index;
                entry.Version = ++_portalVersion;
            }
            foreach (ulong id in update.Removes)
            {
                if (!_portals.TryGetValue(id, out var entry) || entry.Worker != w.Index) continue;
                foreach (var link in entry.Links.ToArray()) ForgetLink(link);
                _portals.Remove(id);
            }
            // A portal's window or far face may have moved: who looks through what is worked out again before anything is sent.
            ReviseWindows();
            SyncAllPortals();
        }

        private void OnPortalLinks(WorkerConn w, PortalLinksUpdate update)
        {
            foreach (var record in update.Upserts)
            {
                if (!_portals.TryGetValue(record.PortalId, out var portal)) continue; // the portal's record always goes first
                if (!_portalLinks.TryGetValue(record.LinkId, out var link))
                {
                    _portalLinks.Add(record.LinkId, link = new LinkEntry());
                    portal.Links.Add(link);
                }
                // Members that are gone lose the pin; the ones that are here keep or gain it.
                foreach (ulong member in link.Members) UnpinMember(member, record.LinkId);
                link.Members.Clear();
                link.Observers.Clear();
                if (record.Members != null) foreach (ulong member in record.Members) link.Members.Add(member);
                if (record.Observers != null) foreach (ulong observer in record.Observers) link.Observers.Add(observer);
                link.Record = record;
                link.Record.Members = null;
                link.Record.Observers = null;
                link.Worker = w.Index;
                link.Version = ++_portalVersion;
                if (record.State != PortalState.Closed) foreach (ulong member in link.Members) PinMember(member, record.LinkId);
            }
            foreach (ulong id in update.Removes)
            {
                if (!_portalLinks.TryGetValue(id, out var link) || link.Worker != w.Index) continue;
                ForgetLink(link);
            }
            // Who looks through what is worked out again from the new records, before anything is sent: a link that closed takes the far side away now.
            ReviseWindows();
            // A member's far row is sent now, ahead of the link, rather than at its next scheduled evaluation.
            foreach (var record in update.Upserts)
                if (_portalLinks.TryGetValue(record.LinkId, out var link))
                    foreach (ulong member in link.Members)
                        if (_clientsById.TryGetValue(member, out var client) && client.Welcomed) { client.InterestDirty = true; SyncOwnershipWindow(client); }
            SyncAllPortals();
        }

        private void PinMember(ulong clientId, ulong linkId)
        {
            if (!_memberLinks.TryGetValue(clientId, out var set)) _memberLinks.Add(clientId, set = new HashSet<ulong>());
            set.Add(linkId);
        }

        private void UnpinMember(ulong clientId, ulong linkId)
        {
            if (_memberLinks.TryGetValue(clientId, out var set) && set.Remove(linkId) && set.Count == 0) _memberLinks.Remove(clientId);
        }

        private void ForgetLink(LinkEntry link)
        {
            ulong id = link.Record.LinkId;
            foreach (ulong member in link.Members) UnpinMember(member, id);
            _portalLinks.Remove(id);
            if (_portals.TryGetValue(link.Record.PortalId, out var portal)) portal.Links.Remove(link);
        }

        /// <summary>A worker is gone: so are the portals it published, and their links.</summary>
        private void DropWorkerPortals(ushort workerIndex)
        {
            if (_portals.Count == 0) return;
            _portalIdScratch.Clear();
            foreach (var kv in _portals) if (kv.Value.Worker == workerIndex) _portalIdScratch.Add(kv.Key);
            foreach (ulong id in _portalIdScratch)
            {
                foreach (var link in _portals[id].Links.ToArray()) ForgetLink(link);
                _portals.Remove(id);
            }
            _portalIdScratch.Clear();
            ReviseWindows();
            SyncAllPortals();
        }

        // ------------------------------------------------------------------------------------------- to clients

        private void SyncAllPortals()
        {
            foreach (var client in _clientsById.Values) SyncPortals(client);
        }

        /// <summary>
        /// Bring one client's portals in line with the container rows it holds (<c>docs/portals.md</c> D45): a portal and its links are sent behind
        /// the row of either face, a link to a member only once it holds the far row, and what no row asks for any more is withdrawn. Sends
        /// nothing when nothing changed.
        /// </summary>
        private void SyncPortals(ClientConn client)
        {
            if (!client.Welcomed) return;
            if (_portals.Count == 0 && client.KnownPortals.Count == 0 && client.KnownPortalLinks.Count == 0) return;
            _portalUpsertScratch.Clear(); _portalRemoveScratch.Clear(); _linkUpsertScratch.Clear(); _linkRemoveScratch.Clear();
            var known = client.KnownContainers;
            foreach (var kv in _portals)
            {
                var portal = kv.Value;
                bool nearKnown = known.Contains(portal.Record.NearContainerId);
                bool any = nearKnown;
                foreach (var link in portal.Links)
                {
                    ulong linkId = link.Record.LinkId;
                    bool sentBefore = client.KnownPortalLinks.TryGetValue(linkId, out int sent);
                    if (!HoldsLink(client, link, nearKnown, sentBefore))
                    {
                        // A link the client held and no longer does.
                        if (sentBefore) { client.KnownPortalLinks.Remove(linkId); _linkRemoveScratch.Add(linkId); }
                        continue;
                    }
                    any = true;
                    if (!sentBefore || sent != link.Version)
                    {
                        client.KnownPortalLinks[linkId] = link.Version;
                        _linkUpsertScratch.Add(ForClient(link, client));
                    }
                }
                if (any)
                {
                    if (!client.KnownPortals.TryGetValue(kv.Key, out int sentPortal) || sentPortal != portal.Version)
                    {
                        client.KnownPortals[kv.Key] = portal.Version;
                        _portalUpsertScratch.Add(portal.Record);
                    }
                }
            }
            // A portal the client held that no row asks for, or that is gone: withdrawn, and its links with it.
            _portalIdScratch.Clear();
            foreach (var kv in client.KnownPortals)
            {
                if (_portals.TryGetValue(kv.Key, out var portal))
                {
                    bool any = known.Contains(portal.Record.NearContainerId);
                    if (!any)
                        foreach (var link in portal.Links)
                            if (client.KnownPortalLinks.ContainsKey(link.Record.LinkId)) { any = true; break; }
                    if (any) continue;
                }
                _portalIdScratch.Add(kv.Key);
            }
            foreach (ulong id in _portalIdScratch)
            {
                client.KnownPortals.Remove(id);
                _portalRemoveScratch.Add(id);
                if (_portals.TryGetValue(id, out var gone))
                    foreach (var link in gone.Links) { client.KnownPortalLinks.Remove(link.Record.LinkId); }
            }
            _portalIdScratch.Clear();
            // Links of portals that no longer exist.
            if (client.KnownPortalLinks.Count > 0)
            {
                _portalIdScratch.Clear();
                foreach (var kv in client.KnownPortalLinks) if (!_portalLinks.ContainsKey(kv.Key)) _portalIdScratch.Add(kv.Key);
                foreach (ulong id in _portalIdScratch) client.KnownPortalLinks.Remove(id);
                _portalIdScratch.Clear();
            }

            if (_portalUpsertScratch.Count > 0 || _portalRemoveScratch.Count > 0)
            {
                _portalWriter.Reset();
                PortalsMsg.Write(_portalWriter, _portalUpsertScratch, _portalRemoveScratch);
                AppendReliable(client, _portalWriter.ToSegment());
            }
            if (_linkUpsertScratch.Count > 0 || _linkRemoveScratch.Count > 0)
            {
                _portalWriter.Reset();
                PortalLinksMsg.Write(_portalWriter, _linkUpsertScratch, _linkRemoveScratch);
                AppendReliable(client, _portalWriter.ToSegment());
            }
        }

        /// <summary>
        /// Whether a client is told of a link: it holds the container row of the far face, or of the near face when it is not a member. A member
        /// waits for the far row, which the gateway keeps pinned for it, so a link never arrives before the row it names; once told, it keeps
        /// being told while it holds the near row, so it learns the link closed after the pin is gone.
        /// </summary>
        private static bool HoldsLink(ClientConn client, LinkEntry link, bool nearKnown, bool sentBefore)
        {
            string far = link.Record.FarEntryId;
            if (!string.IsNullOrEmpty(far) && client.KnownContainers.Contains(far)) return true;
            if (!nearKnown) return false;
            return sentBefore || !link.Members.Contains(client.ClientId);
        }

        /// <summary>A link as one client is told of it: whether it is a member and whether it may look through, and not who else is.</summary>
        private static PortalLinkRecord ForClient(LinkEntry link, ClientConn client)
        {
            var record = link.Record;
            var flags = record.Flags & PortalLinkFlags.ObserverAnyone;
            if (link.Members.Contains(client.ClientId)) flags |= PortalLinkFlags.Member;
            if (link.Observers.Contains(client.ClientId)) flags |= PortalLinkFlags.Observer;
            record.Flags = flags;
            record.Members = null;
            record.Observers = null;
            return record;
        }

        // ------------------------------------------------------------------------------------------- from clients

        /// <summary>
        /// A member client says it holds a link's far window. Forwarded to the worker that owns the portal only from a member of the link, with
        /// the client id stamped on, so a client can never speak for another.
        /// </summary>
        private void OnPortalWarm(ClientConn c, PortalWarmMsg msg)
        {
            if (!_portalLinks.TryGetValue(msg.LinkId, out var link) || !link.Members.Contains(c.ClientId)) return;
            if (!_workersByIndex.TryGetValue(link.Worker, out var w)) return;
            msg.ClientId = c.ClientId;
            _writer.Reset();
            msg.Write(_writer);
            Send(w.PeerId, Delivery.ReliableOrdered, _writer.ToSegment());
        }
    }
}
