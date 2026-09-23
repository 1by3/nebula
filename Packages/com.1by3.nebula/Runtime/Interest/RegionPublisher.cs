using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// The worker's side of interest management: which gateways hear about each region, and the
    /// grouping that lets one serialization serve all of them.
    /// <para>
    /// Every region carries a <b>subscriber mask</b>, one bit per gateway link. Once a tick the publisher groups
    /// the regions that share a mask, so a batch is written once and sent to each gateway in the mask: with G
    /// gateways the work is O(entities + batches × subscribers) instead of O(entities × gateways). Nothing here
    /// remembers what a gateway already knows — a worker's memory must not grow with the number of gateways —
    /// so the only per-gateway state is the region set itself.
    /// </para>
    /// </summary>
    public sealed class RegionPublisher
    {
        /// <summary>Bits in a mask. A mesh with more gateway links than this falls back to per-region sends for the rest.</summary>
        public const int MaxGateways = 64;

        /// <summary>Regions that share one subscriber mask, and therefore one serialization.</summary>
        public struct Group
        {
            /// <summary>Bit per gateway link that wants these regions.</summary>
            public ulong Mask;
            /// <summary>The regions themselves. Owned by the publisher and reused between rebuilds.</summary>
            public List<ulong> Regions;
            /// <summary>Entities bucketed in these regions, counted during the rebuild.</summary>
            public int EntityCount;
        }

        private readonly Dictionary<ulong, ulong> _masks = new Dictionary<ulong, ulong>();
        private readonly RegionSubscriptionReceiver[] _receivers = new RegionSubscriptionReceiver[MaxGateways];
        private readonly List<Group> _groups = new List<Group>();
        private readonly Dictionary<ulong, int> _byMask = new Dictionary<ulong, int>();
        private readonly Stack<List<ulong>> _listPool = new Stack<List<ulong>>();
        private ulong _linked;

        /// <summary>Bit per gateway link currently registered.</summary>
        public ulong LinkedMask => _linked;
        /// <summary>Regions at least one gateway subscribes.</summary>
        public int SubscribedRegions => _masks.Count;
        /// <summary>Groups produced by the last <see cref="BuildGroups{T}"/>.</summary>
        public IReadOnlyList<Group> Groups => _groups;
        /// <summary>Entities in subscribed regions in the last rebuild: what is actually sent.</summary>
        public long FilteredEntities { get; private set; }
        /// <summary>Entities the worker holds in regions at all: what would be sent without interest management.</summary>
        public long TotalEntities { get; private set; }

        public static ulong Bit(int gatewayBit) => (uint)gatewayBit < MaxGateways ? 1UL << gatewayBit : 0UL;

        // ------------------------------------------------------------------------------------------- links

        /// <summary>Register a gateway link and the subscription set it maintains.</summary>
        public void AddGateway(int gatewayBit, RegionSubscriptionReceiver receiver)
        {
            ulong bit = Bit(gatewayBit);
            if (bit == 0) return;
            _linked |= bit;
            _receivers[gatewayBit] = receiver;
            if (receiver == null) return;
            foreach (ulong region in receiver) Subscribe(gatewayBit, region);
        }

        /// <summary>Drop a gateway link: its bit leaves every region it held.</summary>
        public void RemoveGateway(int gatewayBit)
        {
            ulong bit = Bit(gatewayBit);
            if (bit == 0) return;
            var receiver = _receivers[gatewayBit];
            if (receiver != null) foreach (ulong region in receiver) Unsubscribe(gatewayBit, region);
            _receivers[gatewayBit] = null;
            _linked &= ~bit;
        }

        /// <summary>
        /// Fold a committed subscription change into the region masks. Call it from the receiver's change
        /// callback: the journal it exposes is exactly the work to do, so the cost follows the change and not
        /// the size of the set.
        /// </summary>
        public void ApplyChanges(int gatewayBit, RegionSubscriptionReceiver receiver)
        {
            if (receiver == null || Bit(gatewayBit) == 0) return;
            _receivers[gatewayBit] = receiver;
            _linked |= Bit(gatewayBit);
            var removed = receiver.Removed;
            for (int i = 0; i < removed.Count; i++) Unsubscribe(gatewayBit, removed[i]);
            var added = receiver.Added;
            for (int i = 0; i < added.Count; i++) Subscribe(gatewayBit, added[i]);
        }

        public void Subscribe(int gatewayBit, ulong region)
        {
            ulong bit = Bit(gatewayBit);
            if (bit == 0) return;
            _masks.TryGetValue(region, out ulong mask);
            _masks[region] = mask | bit;
        }

        public void Unsubscribe(int gatewayBit, ulong region)
        {
            ulong bit = Bit(gatewayBit);
            if (bit == 0 || !_masks.TryGetValue(region, out ulong mask)) return;
            mask &= ~bit;
            if (mask == 0) _masks.Remove(region);
            else _masks[region] = mask;
        }

        /// <summary>Which gateways want a region (0 = nobody, so nothing in it is sent anywhere).</summary>
        public ulong MaskOf(ulong region) => _masks.TryGetValue(region, out ulong mask) ? mask : 0;

        /// <summary>
        /// An entity moved from region <paramref name="from"/> to <paramref name="to"/>: these gateways have not
        /// heard of it and need a spawn.
        /// </summary>
        public ulong SpawnBits(ulong from, ulong to) => MaskOf(to) & ~MaskOf(from);

        /// <summary>The counterpart: these gateways subscribed the old region only, and are told to forget it.</summary>
        public ulong ForgetBits(ulong from, ulong to) => MaskOf(from) & ~MaskOf(to);

        /// <summary>
        /// Which gateways a wide entity (one whose own radius exceeds a region scan) reaches: the ones with a
        /// focus region within its radius. Matched at the evaluation rate, not per tick.
        /// </summary>
        public ulong WideMask(in InterestGrid grid, double x, double y, double z, double radius) =>
            WideMask(grid, 0UL, x, y, z, radius);

        /// <summary>
        /// <see cref="WideMask(in InterestGrid,double,double,double,double)"/> for an entity of
        /// <paramref name="instanceId"/>. A focus region id is salted with the scope it was collected in
        /// (<see cref="RegionKeys"/>), so unsalting with this entity's scope is what turns the foci of <i>its</i>
        /// scope back into coordinates — and what makes a focus in another world land nowhere near it.
        /// </summary>
        public ulong WideMask(in InterestGrid grid, ulong instanceId, double x, double y, double z, double radius) =>
            WideMaskSalted(grid, RegionKeys.SaltOf(instanceId), x, y, z, radius);

        /// <summary>
        /// <see cref="WideMask(in InterestGrid,ulong,double,double,double,double)"/> for an entity whose region space is
        /// given by its salt (<see cref="RegionKeys.SaltOf(ulong,ulong)"/>): a scope, or a physics frame with regions of
        /// its own inside one. Only foci of the same space come out near it.
        /// </summary>
        public ulong WideMaskSalted(in InterestGrid grid, ulong salt, double x, double y, double z, double radius)
        {
            ulong mask = 0;
            double r2 = radius * radius;
            for (int bit = 0; bit < MaxGateways; bit++)
            {
                var receiver = _receivers[bit];
                if (receiver == null) continue;
                var foci = receiver.FociRegions;
                for (int i = 0; i < foci.Count; i++)
                    if (grid.SqrDistanceToRegion(foci[i] ^ salt, x, y, z) <= r2) { mask |= 1UL << bit; break; }
            }
            return mask;
        }

        /// <summary>Which gateways explicitly subscribed an entity by id (a policy extra, or one they own).</summary>
        public ulong ExplicitMask(ulong netId)
        {
            ulong mask = 0;
            for (int bit = 0; bit < MaxGateways; bit++)
            {
                var receiver = _receivers[bit];
                if (receiver == null) continue;
                var ids = receiver.Entities;
                for (int i = 0; i < ids.Count; i++)
                    if (ids[i] == netId) { mask |= 1UL << bit; break; }
            }
            return mask;
        }

        // ------------------------------------------------------------------------------------------- grouping

        /// <summary>
        /// Group this tick's subscribed regions by mask. Allocation free after the first few ticks: the region
        /// lists are pooled and reused. Iterate <see cref="Groups"/>, write one batch per group, and send it to
        /// every gateway in <see cref="Group.Mask"/>.
        /// </summary>
        public void BuildGroups<T>(InterestIndex<T> index)
        {
            for (int i = 0; i < _groups.Count; i++) { _groups[i].Regions.Clear(); _listPool.Push(_groups[i].Regions); }
            _groups.Clear();
            _byMask.Clear();
            FilteredEntities = 0;
            TotalEntities = 0;
            if (index == null) return;
            foreach (ulong region in index.Regions)
            {
                int count = index.Region(region).Count;
                TotalEntities += count;
                ulong mask = MaskOf(region);
                if (mask == 0) continue;
                FilteredEntities += count;
                if (!_byMask.TryGetValue(mask, out int at))
                {
                    at = _groups.Count;
                    _byMask[mask] = at;
                    _groups.Add(new Group { Mask = mask, Regions = _listPool.Count > 0 ? _listPool.Pop() : new List<ulong>(16) });
                }
                var group = _groups[at];
                group.Regions.Add(region);
                group.EntityCount += count;
                _groups[at] = group;
            }
        }

        /// <summary>Forget every link and mask (the worker is shutting down, or lost every gateway).</summary>
        public void Clear()
        {
            _masks.Clear();
            Array.Clear(_receivers, 0, _receivers.Length);
            for (int i = 0; i < _groups.Count; i++) { _groups[i].Regions.Clear(); _listPool.Push(_groups[i].Regions); }
            _groups.Clear();
            _byMask.Clear();
            _linked = 0;
            FilteredEntities = 0;
            TotalEntities = 0;
        }
    }
}
