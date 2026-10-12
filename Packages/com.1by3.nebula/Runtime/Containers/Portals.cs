using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Lookup for portals (<c>docs/portals.md</c> D1): the <see cref="Portal"/> components on this process, and, on a client, what the
    /// network gateway has told it about the portals near it. Link poses (<c>Map</c>, <c>Turn</c>) and per-scope drawing slots
    /// are added by later milestones of the same design.
    /// </summary>
    public static class Portals
    {
        private static readonly List<Portal> Registered = new List<Portal>();

        /// <summary>Every enabled portal component on this process.</summary>
        public static IReadOnlyList<Portal> All => Registered;

        internal static void Register(Portal portal)
        {
            if (!Registered.Contains(portal)) Registered.Add(portal);
        }

        internal static void Unregister(Portal portal) => Registered.Remove(portal);

        /// <summary>The enabled portal component with this derived id (<see cref="Portal.PortalId"/>), or null.</summary>
        public static Portal Find(ulong portalId)
        {
            for (int i = 0; i < Registered.Count; i++)
            {
                var portal = Registered[i];
                if (portal != null && !portal.IsIgnored && portal.PortalId == portalId) return portal;
            }
            return null;
        }

        /// <summary>The live link with this id (<see cref="PortalLink.Id"/>) of any enabled portal on this process, or null.</summary>
        public static PortalLink FindLink(ulong linkId)
        {
            for (int i = 0; i < Registered.Count; i++)
            {
                var portal = Registered[i];
                if (portal == null || portal.IsIgnored) continue;
                foreach (var link in portal.Links) if (link.Id == linkId) return link;
            }
            return null;
        }

        /// <summary>
        /// Whether a link that is not closed leads into this container (<c>docs/portals.md</c> D43): a preparing, open or closing link keeps
        /// its far scope from being judged idle, as an occupant does, so a door left open onto an empty room does not lose the room.
        /// </summary>
        internal static bool HoldsFarContainer(Container container)
        {
            for (int i = 0; i < Registered.Count; i++)
            {
                var portal = Registered[i];
                if (portal == null || portal.IsIgnored || portal.LinkCount == 0) continue;
                foreach (var link in portal.Links)
                    if (link.State != PortalState.Closed && link.FarEntry != null && link.FarEntry == container) return true;
            }
            return false;
        }

        /// <summary>
        /// Test the entities a worker simulates against the portals it owns, once a tick, after the entity's own simulation
        /// (<c>docs/portals.md</c> D15). Only player-owned pawns cross today; the swept test for every other entity is NEB-429's.
        /// </summary>
        internal static void Tick(NebulaWorker worker, NetworkIdentity entity)
        {
            // A pinned entity keeps its container (docs/frame-bodies.md D8): no portal moves it into another scope.
            if (entity.OwnerClientId == 0 || !entity.HasAuthority || entity.ContainerPinned) return;
            for (int i = 0; i < Registered.Count; i++)
            {
                var portal = Registered[i];
                if (portal != null && !portal.IsIgnored) portal.Cross(worker, entity);
            }
        }

        // ------------------------------------------------------------------------------------------ on a client

        private static readonly Dictionary<ulong, PortalRecord> KnownPortals = new Dictionary<ulong, PortalRecord>();
        private static readonly Dictionary<ulong, PortalLinkRecord> KnownLinkRecords = new Dictionary<ulong, PortalLinkRecord>();

        /// <summary>
        /// On a client: the portals the network gateway has told it about, by derived id. A portal is sent behind the container row of
        /// either of its faces and withdrawn with the row (<c>docs/portals.md</c> D45).
        /// </summary>
        public static IReadOnlyDictionary<ulong, PortalRecord> Known => KnownPortals;

        /// <summary>
        /// On a client: the state of each link the network gateway has told it about, by link id. A link is sent to the clients that
        /// hold the container row of either face, with whether this client is a member and whether it may look through.
        /// </summary>
        public static IReadOnlyDictionary<ulong, PortalLinkRecord> KnownLinks => KnownLinkRecords;

        /// <summary>Raised on a client when a portal record or a link record arrived or was withdrawn.</summary>
        public static event Action KnownChanged;

        internal static void ApplyRecords(PortalsUpdate update)
        {
            foreach (var record in update.Upserts) KnownPortals[record.PortalId] = record;
            foreach (ulong id in update.Removes)
            {
                KnownPortals.Remove(id);
                // A portal's links go with it.
                List<ulong> gone = null;
                foreach (var kv in KnownLinkRecords) if (kv.Value.PortalId == id) (gone ??= new List<ulong>()).Add(kv.Key);
                if (gone != null) foreach (ulong linkId in gone) KnownLinkRecords.Remove(linkId);
            }
            RaiseKnownChanged();
        }

        internal static void ApplyLinks(PortalLinksUpdate update)
        {
            foreach (var record in update.Upserts) KnownLinkRecords[record.LinkId] = record;
            foreach (ulong id in update.Removes) KnownLinkRecords.Remove(id);
            RaiseKnownChanged();
        }

        internal static void ResetForNewSession()
        {
            Registered.Clear();
            ResetClient();
        }

        internal static void ResetClient()
        {
            if (KnownPortals.Count == 0 && KnownLinkRecords.Count == 0) return;
            KnownPortals.Clear();
            KnownLinkRecords.Clear();
            RaiseKnownChanged();
        }

        private static void RaiseKnownChanged()
        {
            var handler = KnownChanged;
            if (handler == null) return;
            try { handler(); }
            catch (Exception e) { NebulaLog.Error($"a Portals.KnownChanged handler threw: {e}"); }
        }
    }
}
