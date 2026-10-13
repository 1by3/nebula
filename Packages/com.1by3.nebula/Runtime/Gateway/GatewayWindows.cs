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
    /// Observation through an open portal, both ways (<c>docs/portals.md</c> D20 to D24), seen from the network gateway. Each side of an Open
    /// or Closing link has a window (<see cref="PortalWindow"/>). A client qualifies on a side when it may look through the link (it is on the
    /// link's observer list, or the list is <c>Anyone</c>), its pawn is in that side's scope and its pawn is inside that side's window. It then
    /// receives the other side's window: the container rows inside it, the carriers of those rows out to the root of their chain, and every
    /// entity standing in it, subscribed beside the client's own regions with the other side's scope salt. Everything is decided from the
    /// portal and link records the worker published, on the network gateway's own entity records, so no game code runs here. Windows do not
    /// chain: a client is told of the scope on the other side of the links it stands in the window of, never of one two links away (D22).
    /// </summary>
    public sealed partial class NebulaGateway
    {
        /// <summary>
        /// Most windows one client looks through at once, nearest first (<c>docs/portals.md</c> D22). A client standing where more links'
        /// windows overlap sees through the nearest this many.
        /// </summary>
        public const int MaxPortalWindows = 4;

        /// <summary>A window's focus reaches this fraction of a normal focus beyond its box: authorization decides what is inside the window, so the focus only has to find it.</summary>
        private const float WindowFocusScale = 0.05f;

        /// <summary>One window one client looks through: the side its pawn is in, the side it receives, and the link between them.</summary>
        private struct ClientWindow
        {
            public ulong LinkId;
            public ulong PortalId;
            /// <summary>The window the client's pawn is inside.</summary>
            public PortalWindow Home;
            /// <summary>The window the client receives: the other side's.</summary>
            public PortalWindow Other;
            /// <summary>How far the pawn is from the home face, for the nearest-first order.</summary>
            public double Distance;
        }

        /// <summary>A window seen in a frame with regions of its own (a planet that is a carrier): the window's box in the frame's coordinates, where the entities standing on its ground are bucketed (<c>docs/container-tree.md</c> D18).</summary>
        private struct FrameWindow
        {
            public ulong FrameKey;
            public ulong Scope;
            public Vector3 Center, Size;
        }

        [Flags]
        private enum WindowChange { None = 0, Added = 1, Removed = 2 }

        private readonly List<ulong> _windowIdScratch = new List<ulong>();
        private readonly List<Container> _windowContainerScratch = new List<Container>();

        /// <summary>
        /// How many windows this gateway's clients look through right now, in all (<c>docs/portals.md</c> D22): what an operator reads to see how
        /// much of the mesh's interest is spent on portals.
        /// </summary>
        public int PortalWindowCount
        {
            get
            {
                int n = 0;
                foreach (var client in _clientsById.Values) n += client.Windows.Count;
                return n;
            }
        }

        /// <summary>How many windows one client looks through right now (<see cref="InterestClient.PortalWindows"/>); 0 for a client this gateway does not know.</summary>
        public int PortalWindowsOf(ulong clientId) => _clientsById.TryGetValue(clientId, out var client) ? client.Windows.Count : 0;

        /// <summary>The scope an entity, or the root carrier it rides in, stands in, as an absolute point of that scope (a planet's frame composed through).</summary>
        private Double3 ScopePointOf(EntityRecord rec)
        {
            var root = RootOf(rec);
            if (root.FrameKey == 0) return new Double3(root.AbsX, root.AbsY, root.AbsZ);
            return WorldPosition(root.Container, root.LastSpawn.LocalPosition);
        }

        /// <summary>
        /// Work out which windows a client looks through, from the link records and its pawn's place (<c>docs/portals.md</c> D20): every Open
        /// or Closing link it may look through whose home-side window holds its pawn, nearest first, at most <see cref="MaxPortalWindows"/>. With
        /// <paramref name="carriers"/> it also collects the carriers the rows inside those windows are fixed in. Costs one pass over the links.
        /// </summary>
        private WindowChange RefreshWindows(ClientConn client, bool carriers)
        {
            var windows = client.Windows;
            _windowIdScratch.Clear();
            for (int i = 0; i < windows.Count; i++) _windowIdScratch.Add(windows[i].LinkId);
            windows.Clear();
            if (carriers) client.WindowCarriers.Clear();
            if (_portalLinks.Count > 0 && client.Welcomed && client.PawnNetId != 0 && _entities.TryGetValue(client.PawnNetId, out var pawn))
            {
                ulong scope = ScopeOfClient(client);
                var point = ScopePointOf(pawn);
                foreach (var link in _portalLinks.Values)
                {
                    var record = link.Record;
                    if (record.State != PortalState.Open && record.State != PortalState.Closing) continue;
                    if (!link.Observers.Contains(client.ClientId) && (record.Flags & PortalLinkFlags.ObserverAnyone) == 0) continue;
                    if (!_portals.TryGetValue(record.PortalId, out var portal)) continue;
                    var portalRecord = portal.Record;
                    ulong farScope = record.FarScope != 0 ? record.FarScope : portalRecord.FarScope;
                    if (farScope == 0 || farScope == portalRecord.NearScope) continue; // one scope: nothing to look through to (a portal between two places of one scope is the offset portal of docs/portals.md §15, milestone 7)
                    PortalWindow home, other;
                    if (scope == portalRecord.NearScope) { home = portalRecord.NearWindow; other = portalRecord.FarWindow(farScope); }
                    else if (scope == farScope) { home = portalRecord.FarWindow(farScope); other = portalRecord.NearWindow; }
                    else continue;
                    if (other.IsEmpty || !home.Contains(point)) continue;
                    var from = home.Origin;
                    double dx = point.X - from.X, dy = point.Y - from.Y, dz = point.Z - from.Z;
                    var window = new ClientWindow { LinkId = record.LinkId, PortalId = record.PortalId, Home = home, Other = other, Distance = dx * dx + dy * dy + dz * dz };
                    // Nearest first, and the farther ones are the ones a full budget leaves out.
                    int at = windows.Count;
                    while (at > 0 && windows[at - 1].Distance > window.Distance) at--;
                    if (at >= MaxPortalWindows) continue;
                    windows.Insert(at, window);
                    if (windows.Count > MaxPortalWindows) windows.RemoveAt(windows.Count - 1);
                }
                if (carriers)
                    for (int i = 0; i < windows.Count; i++) CollectWindowCarriers(client, windows[i].Other);
            }
            client.FrameWindows.Clear();
            if (client.Windows.Count > 0)
                for (int i = 0; i < windows.Count; i++) CollectFrameWindows(client, windows[i].Other, carriers);
            var change = WindowChange.None;
            if (windows.Count != _windowIdScratch.Count) change |= windows.Count > _windowIdScratch.Count ? WindowChange.Added : WindowChange.Removed;
            for (int i = 0; i < _windowIdScratch.Count; i++)
            {
                bool still = false;
                for (int j = 0; j < windows.Count && !still; j++) still = windows[j].LinkId == _windowIdScratch[i];
                if (!still) change |= WindowChange.Removed;
            }
            for (int j = 0; j < windows.Count; j++)
                if (!_windowIdScratch.Contains(windows[j].LinkId)) change |= WindowChange.Added;
            _windowIdScratch.Clear();
            return change;
        }

        /// <summary>
        /// The rows inside a window are fixed in a carrier when the ground they stand on is a planet that is itself a carrier: the carriers of
        /// those rows, out to the root of their chain, are kept in the client's set, so the rows can be built (NEB-426, <c>docs/portals.md</c> D20).
        /// Only these, never the carrier's other interest.
        /// </summary>
        private void CollectWindowCarriers(ClientConn client, in PortalWindow window)
        {
            window.Bounds(out var min, out var max);
            var box = InRegistryFrame(new Double3((min.X + max.X) * 0.5, (min.Y + max.Y) * 0.5, (min.Z + max.Z) * 0.5),
                new Vector3((float)(max.X - min.X), (float)(max.Y - min.Y), (float)(max.Z - min.Z)), window.Scope);
            ContainerRegistry.Overlapping(box, _windowContainerScratch, window.Scope);
            for (int i = 0; i < _windowContainerScratch.Count; i++) CollectCarriersOfRow(client, _windowContainerScratch[i].ContainerId);
            _windowContainerScratch.Clear();
        }

        /// <summary>
        /// A window onto ground that is a frame with regions of its own (a planet that is a carrier): what stands on that ground is bucketed in the
        /// frame's coordinates, so the window is also a box there. The frame's axes are sampled as <see cref="AddFrameApproachFoci"/> does
        /// (<see cref="FrameAxes"/>), and the window's corners are carried into the frame by their inverse, as offsets from the frame's origin taken in double. Only
        /// frames in the window's scope that stand directly in its space are used; a frame nested in another is not followed.
        /// </summary>
        private void CollectFrameWindows(ClientConn client, in PortalWindow other, bool carriers)
        {
            if (_ownRegionCarriers.Count == 0 && _ownRegionFrames.Count == 0) return;
            foreach (ulong netId in _ownRegionCarriers)
            {
                if (!_entities.TryGetValue(netId, out var carrier) || InstanceOf(carrier) != other.Scope) continue;
                AddFrameWindow(client, ContainerRef.Dynamic(netId), other, carriers);
            }
            foreach (string id in _ownRegionFrames)
            {
                var frame = RefOfId(id);
                if (frame.IsNone || frame.IsRuntime || frame.IsDynamic) continue;
                var box = ContainerRegistry.Resolve(frame);
                if (box == null || box.InstanceId != other.Scope) continue;
                AddFrameWindow(client, frame, other, carriers);
            }
        }

        private void AddFrameWindow(ClientConn client, ContainerRef frame, in PortalWindow other, bool carriers)
        {
            ulong frameKey = RegionKeys.FrameKeyOf(frame);
            var origin = Double3.Zero;
            ulong outerKey = frameKey;
            var lifted = frame;
            if (!TryLiftOut(ref lifted, ref origin, ref outerKey) || outerKey != 0) return;
            if (!FrameAxes(frame, frameKey, origin, out var map)) return;
            var inverse = map.inverse;
            other.Bounds(out var min, out var max);
            var low = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var high = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                // An offset from the frame's origin, taken in double: the corner and the origin are both 10^8 m out in a star system.
                var corner = inverse.MultiplyPoint3x4((new Double3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z) - origin).ToVector3());
                low = Vector3.Min(low, corner);
                high = Vector3.Max(high, corner);
            }
            // Only if the window reaches into the frame at all: a planet's box is large, but a window beside it holds none of its ground.
            FrameExtent(frame, frameKey, out var extentMin, out var extentMax);
            bool extentKnown = extentMax.x > extentMin.x || extentMax.y > extentMin.y || extentMax.z > extentMin.z;
            if (extentKnown && (high.x < extentMin.x || low.x > extentMax.x || high.y < extentMin.y || low.y > extentMax.y || high.z < extentMin.z || low.z > extentMax.z)) return;
            var window = new FrameWindow { FrameKey = frameKey, Scope = other.Scope, Center = (low + high) * 0.5f, Size = high - low };
            client.FrameWindows.Add(window);
            if (!carriers) return;
            var frameBox = new Bounds(window.Center, window.Size);
            foreach (var kv in _ownershipById)
            {
                var row = kv.Value;
                if (!row.HasPlacement || row.Placement.IsRoot || !ContainerRegistry.IsDynamicId(row.Placement.ParentId)) continue;
                if (RegionKeys.FrameKeyOf(ContainerRef.Dynamic(ContainerRegistry.CarrierNetIdOf(row.Placement.ParentId))) != frameKey) continue;
                if (frameBox.Intersects(new Bounds(row.Placement.Center.ToVector3(), row.Placement.Size))) CollectCarriersOfRow(client, kv.Key);
            }
        }

        /// <summary>The carrier a row is fixed in (its parent chain ends in a carried container's box), and that carrier's own carriers out to the root. A root row has none.</summary>
        private void CollectCarriersOfRow(ClientConn client, string rowId)
        {
            string id = rowId;
            for (int up = 0; up <= _ownershipById.Count && _ownershipById.TryGetValue(id, out var row); up++)
            {
                if (!row.HasPlacement || row.Placement.IsRoot) return;
                if (ContainerRegistry.IsDynamicId(row.Placement.ParentId))
                {
                    ulong carrier = ContainerRegistry.CarrierNetIdOf(row.Placement.ParentId);
                    for (int hops = 0; carrier != 0 && hops <= _entities.Count && client.WindowCarriers.Add(carrier); hops++)
                    {
                        if (!_entities.TryGetValue(carrier, out var rec) || !TryCarrierOf(rec.Container, out ulong next, out _)) return;
                        carrier = next;
                    }
                    return;
                }
                id = row.Placement.ParentId;
            }
        }

        /// <summary>
        /// The rule <see cref="CanObserve"/> adds (<c>docs/portals.md</c> D21): an entity in scope <paramref name="targetScope"/> is observable
        /// when it stands inside the other side's window of a link the client looks through. Decided on the entity's root, so a passenger is judged
        /// where its carrier is, from the link records and the gateway's own entity records alone.
        /// </summary>
        private bool ObservableThroughWindow(ClientConn client, EntityRecord entity, ulong targetScope)
        {
            var windows = client.Windows;
            bool placed = false;
            Double3 point = default;
            for (int i = 0; i < windows.Count; i++)
            {
                if (windows[i].Other.Scope != targetScope) continue;
                if (!placed) { point = ScopePointOf(entity); placed = true; }
                if (windows[i].Other.Contains(point)) return true;
            }
            return false;
        }

        /// <summary>
        /// A window's focus for each window the client looks through (<c>docs/portals.md</c> D23): a box in the other side's scope, so the client's
        /// subscriptions span both scopes, the entities found are candidates for <see cref="CanObserve"/>, and the container rows come after the client's own.
        /// </summary>
        private void AddPortalWindows(ClientConn client, InterestQuery query)
        {
            var windows = client.Windows;
            for (int i = 0; i < windows.Count; i++)
            {
                var other = windows[i].Other;
                other.Bounds(out var min, out var max);
                query.AddFocus(InterestFocus.Box((min.X + max.X) * 0.5, (min.Y + max.Y) * 0.5, (min.Z + max.Z) * 0.5,
                    max.X - min.X, max.Y - min.Y, max.Z - min.Z, WindowFocusScale).InScope(other.Scope));
            }
            // And the same window in the frame of any planet it reaches into, where the ground's entities are bucketed (docs/container-tree.md D18).
            var frames = client.FrameWindows;
            for (int i = 0; i < frames.Count; i++)
            {
                var focus = InterestFocus.Box(frames[i].Center.x, frames[i].Center.y, frames[i].Center.z, frames[i].Size.x, frames[i].Size.y, frames[i].Size.z, WindowFocusScale).InScope(frames[i].Scope);
                focus.Space = frames[i].FrameKey;
                query.AddFocus(focus);
            }
        }

        /// <summary>
        /// Portal or link records changed (<c>docs/portals.md</c> D21): recompute every client's windows now. A client that lost a window is
        /// re-authorized before anything else is relayed, so closing a link takes the far side away at once; one that gained a window is
        /// evaluated at the next tick.
        /// </summary>
        private void ReviseWindows()
        {
            foreach (var client in _clientsById.Values)
            {
                if (!client.Welcomed) continue;
                if (_portalLinks.Count == 0 && client.Windows.Count == 0) continue;
                var change = RefreshWindows(client, true);
                if ((change & WindowChange.Removed) != 0) RevalidateInterest(client);
                else if (change != WindowChange.None) client.InterestDirty = true;
            }
        }
    }
}
