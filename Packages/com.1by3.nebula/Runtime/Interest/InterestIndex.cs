using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>Where an item sits in an <see cref="InterestIndex{T}"/>.</summary>
    public enum InterestPlacement : byte
    {
        /// <summary>In one region bucket, found by a client's region scan.</summary>
        Region = 0,
        /// <summary>
        /// Its relevance radius is larger than the scan, so widening every client's scan to find it would cost
        /// every client. It lives in a short list that is tested directly against each focus instead.
        /// </summary>
        Wide = 1,
        /// <summary>Always relevant: in everyone's set, never distance tested.</summary>
        Global = 2,
    }

    /// <summary>The outcome of linking an item to a carrier.</summary>
    public enum CarrierLink : byte
    {
        /// <summary>The item already had this carrier; nothing was changed.</summary>
        Unchanged = 0,
        /// <summary>The item now rides in the carrier and was placed with it.</summary>
        Linked = 1,
        /// <summary>The item was taken out of the carrier it was in and placed on its own again.</summary>
        Detached = 2,
        /// <summary>No such item in the index; nothing was remembered. Link it after adding it.</summary>
        UnknownItem = 3,
        /// <summary>
        /// The link was refused because it would close a carrier cycle (something cannot ride in itself, directly
        /// or through any chain). Nothing was changed: the item keeps the carrier it had. Report it — a cycle is a
        /// bug in whatever decides what is inside what, and silently accepting one makes "the whole subtree"
        /// meaningless.
        /// </summary>
        Cycle = 4,
    }

    /// <summary>
    /// Region → dense list of items, with swap-remove and O(1) rebucketing. Used by the gateway for its entity
    /// records and by the worker for its authoritative entities, so both sides answer "what is near this focus"
    /// the same way.
    /// <para>
    /// Carried entities are bucketed in their <b>root carrier's</b> region and move with it: a ship and
    /// everything riding in it enter and leave a client's set as one unit at any speed. Bucketing a passenger by
    /// its own resolved position would let a seat straddle a region edge and pop separately from its ship.
    /// </para>
    /// <para>
    /// Placement follows the root carrier too, not only the region: an item's own
    /// <see cref="Add"/>/<see cref="AddWide"/>/<see cref="AddGlobal"/> call is remembered as its <i>own</i>
    /// placement and is what it reverts to when it is no longer carried, but while it rides in something it sits
    /// exactly where its root carrier sits. An always-relevant passenger of an ordinary ship is therefore a region
    /// item in the ship's region, not a global one — otherwise the subtree would not enter and leave as one unit,
    /// and every gateway in the mesh would be sent a crate because of the crate's own prefab settings.
    /// </para>
    /// <para>
    /// Steady state allocates nothing: bucket lists are pooled and reused, and a region is removed from the map
    /// (its list returned to the pool) only when it empties, which is also what keeps <see cref="RegionCount"/>
    /// meaningful for the subscriber-mask work on the worker.
    /// </para>
    /// </summary>
    public sealed class InterestIndex<T>
    {
        /// <summary>One item in a bucket: the entity's net id and whatever record the owner of the index keeps.</summary>
        public struct Entry
        {
            public ulong Id;
            public T Value;
        }

        private struct Location
        {
            /// <summary>Where the item actually sits: its own placement, or its root carrier's while it is carried.</summary>
            public InterestPlacement Where;
            public ulong Region;
            public int Slot;
            /// <summary>Direct carrier (0 = none). The item is bucketed in the root carrier's region.</summary>
            public ulong Carrier;
            public bool HasChildren;
            /// <summary>What the owner of the index asked for, and what the item reverts to when it stops being carried.</summary>
            public InterestPlacement Own;
            public ulong OwnRegion;
        }

        private readonly Dictionary<ulong, Location> _locations = new Dictionary<ulong, Location>();
        private readonly Dictionary<ulong, List<Entry>> _regions = new Dictionary<ulong, List<Entry>>();
        private readonly Dictionary<ulong, List<ulong>> _children = new Dictionary<ulong, List<ulong>>();
        private readonly List<Entry> _wide = new List<Entry>();
        private readonly List<Entry> _global = new List<Entry>();
        private readonly Stack<List<Entry>> _entryPool = new Stack<List<Entry>>();
        private readonly Stack<List<ulong>> _idPool = new Stack<List<ulong>>();
        /// <summary>Work stack for subtree walks; reused so a rebucket of a deep carrier allocates nothing.</summary>
        private readonly List<ulong> _walk = new List<ulong>();
        /// <summary>Children of a carrier that is being removed; reused, and never the walk list (it is in use).</summary>
        private readonly List<ulong> _orphans = new List<ulong>();

        public int Count => _locations.Count;
        /// <summary>Regions holding at least one item.</summary>
        public int RegionCount => _regions.Count;
        /// <summary>Every non-empty region. Enumerating this is allocation free.</summary>
        public Dictionary<ulong, List<Entry>>.KeyCollection Regions => _regions.Keys;
        public int WideCount => _wide.Count;
        public int GlobalCount => _global.Count;

        /// <summary>Items of one region (empty when nothing is there). Allocation free.</summary>
        public Bucket Region(ulong region) => new Bucket(_regions.TryGetValue(region, out var list) ? list : null);
        /// <summary>Entities whose own relevance radius exceeds the scan radius; tested against each focus directly.</summary>
        public Bucket Wide => new Bucket(_wide);
        /// <summary>Entities that are in every set.</summary>
        public Bucket Global => new Bucket(_global);

        public bool Contains(ulong id) => _locations.ContainsKey(id);

        public bool TryGetValue(ulong id, out T value)
        {
            if (_locations.TryGetValue(id, out var loc)) { value = Read(loc); return true; }
            value = default;
            return false;
        }

        /// <summary>Where the item sits, and in which region when it is bucketed in one.</summary>
        public bool TryGetPlacement(ulong id, out InterestPlacement placement, out ulong region)
        {
            if (_locations.TryGetValue(id, out var loc)) { placement = loc.Where; region = loc.Region; return true; }
            placement = InterestPlacement.Region;
            region = 0;
            return false;
        }

        /// <summary>
        /// What the item asked for itself, ignoring anything it rides in: what <see cref="TryGetPlacement"/> would
        /// say once it is no longer carried. For diagnostics and for a caller that has to tell the two apart.
        /// </summary>
        public bool TryGetOwnPlacement(ulong id, out InterestPlacement placement, out ulong region)
        {
            if (_locations.TryGetValue(id, out var loc)) { placement = loc.Own; region = loc.OwnRegion; return true; }
            placement = InterestPlacement.Region;
            region = 0;
            return false;
        }

        /// <summary>The item's direct carrier, or 0 when it is not carried.</summary>
        public ulong CarrierOf(ulong id) => _locations.TryGetValue(id, out var loc) ? loc.Carrier : 0;

        /// <summary>
        /// The outermost carrier this item rides in — the one whose placement the whole subtree takes. An item that
        /// is not carried is its own root, and so is one whose carrier is not in the index (yet): that is as far up
        /// as this index can see, and it is what the item is placed by until the carrier arrives.
        /// </summary>
        public ulong RootOf(ulong id)
        {
            if (!_locations.TryGetValue(id, out var loc)) return id;
            ulong root = id;
            // Links are acyclic (SetCarrier refuses anything else), so the chain cannot be longer than the index.
            for (int guard = 0; guard <= _locations.Count && loc.Carrier != 0; guard++)
            {
                if (!_locations.TryGetValue(loc.Carrier, out var parent)) break;
                root = loc.Carrier;
                loc = parent;
            }
            return root;
        }

        /// <summary>
        /// How many carriers this item rides inside: 0 for something standing in the world, 1 inside a ship, 2
        /// inside a crate inside that ship. The chain is acyclic (<see cref="SetCarrier"/> refuses anything
        /// else), so the index's own size is the only bound needed and no valid tree is ever cut short — which
        /// is what lets publication order a subtree of any depth.
        /// </summary>
        public int DepthOf(ulong id)
        {
            if (!_locations.TryGetValue(id, out var loc)) return 0;
            int depth = 0;
            while (loc.Carrier != 0 && depth <= _locations.Count)
            {
                if (!_locations.TryGetValue(loc.Carrier, out var parent)) break;
                depth++;
                loc = parent;
            }
            return depth;
        }

        /// <summary>
        /// Whether anything is riding in this item, so a caller can skip the subtree work entirely. True for an
        /// item that is <b>not</b> in the index too: a passenger can name a carrier that has not arrived yet, and
        /// that pending subtree is exactly what has to be republished the moment the carrier does arrive.
        /// </summary>
        public bool HasCarried(ulong id) => _children.ContainsKey(id);

        /// <summary>
        /// Append everything <paramref name="id"/> carries, to any depth, to <paramref name="result"/>, each
        /// carrier before what it carries. That order is the one publication needs: a spawn walks it forwards so a
        /// container always arrives before its contents, a forget backwards so contents leave before their
        /// container. Returns how many ids were appended.
        /// <para>
        /// The <b>whole</b> subtree is collected at any width and any depth. There is no cap, because a partly
        /// walked tree is a partly rebucketed ship — some of the crates published to the gateways at the
        /// destination and the rest left addressed to the ones at the origin — and nothing downstream could tell.
        /// What makes that safe is that the links are acyclic: <see cref="SetCarrier"/> refuses a link that would
        /// close a cycle, so a walk cannot revisit an item and the subtree is at most the rest of the index. That
        /// count is the only bound here, and it stops a corrupted link set rather than a large valid one.
        /// </para>
        /// <para>
        /// The carrier itself need not be in the index: children are remembered from the moment they name it, so
        /// this is also how the subtree waiting for a missing ship is walked — which is what a publisher needs to
        /// announce the transition the ship's arrival causes.
        /// </para>
        /// <para>Allocation free: the caller's list is also the walk frontier, and nothing else is touched.</para>
        /// </summary>
        public int CollectCarried(ulong id, List<ulong> result)
        {
            if (result == null) return 0;
            int start = result.Count;
            if (!_children.TryGetValue(id, out var kids)) return 0;
            result.AddRange(kids);
            int limit = _locations.Count;
            for (int i = start; i < result.Count; i++)
            {
                ulong child = result[i];
                if (child == id || !_children.TryGetValue(child, out var grandKids)) continue;
                for (int k = 0; k < grandKids.Count; k++) if (grandKids[k] != id) result.Add(grandKids[k]);
                if (result.Count - start > limit) break; // unreachable for an acyclic tree; a bound, not a cap
            }
            return result.Count - start;
        }

        // ------------------------------------------------------------------------------------------- mutation

        /// <summary>
        /// Add or update an item in a region. This is the item's <i>own</i> placement: while it rides in something
        /// it stays wherever its root carrier is, and this is what it comes back to when it gets off.
        /// </summary>
        public void Add(ulong id, ulong region, in T value) => Place(id, InterestPlacement.Region, region, value);

        /// <summary>Add or update an item in the wide list (its own relevance radius decides who hears about it).</summary>
        public void AddWide(ulong id, in T value) => Place(id, InterestPlacement.Wide, 0, value);

        /// <summary>Add or update an always-relevant item.</summary>
        public void AddGlobal(ulong id, in T value) => Place(id, InterestPlacement.Global, 0, value);

        private void Place(ulong id, InterestPlacement where, ulong region, in T value)
        {
            if (where != InterestPlacement.Region) region = 0;
            if (_locations.TryGetValue(id, out var loc))
            {
                Write(loc, id, value); // the record first, wherever it currently sits
                loc.Own = where;
                loc.OwnRegion = region;
                _locations[id] = loc;
                Resettle(id);
                return;
            }
            // Something may already be linked to this id as its carrier: children are remembered even while the
            // carrier is missing (a passenger can be indexed before its ship), and this is where they land.
            var fresh = new Location { Own = where, OwnRegion = region, HasChildren = _children.ContainsKey(id) };
            Attach(id, where, region, value, ref fresh);
            _locations[id] = fresh;
            if (fresh.HasChildren) ResettleChildren(id, where, region);
        }

        /// <summary>
        /// Rebucket an item whose region key changed. Its carried subtree moves with it. Returns false for an
        /// unknown item, for a carried one (which is bucketed with its carrier and must not move by itself), and
        /// for one whose own placement is wide or global (its reach, not a bucket, decides who hears about it).
        /// </summary>
        public bool Move(ulong id, ulong region)
        {
            if (!_locations.TryGetValue(id, out var loc) || loc.Carrier != 0) return false;
            if (loc.Own != InterestPlacement.Region) return false;
            if (loc.Where == InterestPlacement.Region && loc.Region == region && loc.OwnRegion == region) return true;
            loc.OwnRegion = region;
            _locations[id] = loc;
            Resettle(id);
            return true;
        }

        /// <summary>Replace the record kept for an item, leaving it where it is.</summary>
        public bool SetValue(ulong id, in T value)
        {
            if (!_locations.TryGetValue(id, out var loc)) return false;
            Write(loc, id, value);
            return true;
        }

        /// <summary>
        /// Link a carried item to the item carrying it (0 detaches). The child — and everything riding in the
        /// child — is placed where the root carrier sits at once, and follows it from then on. The carrier need
        /// not be in the index yet; the link is remembered either way, and the subtree is placed the moment the
        /// carrier is added or moves. Getting off puts the item back on its own placement: a wide or global item
        /// returns to its list, and a region one stays in the bucket it was put down in until the next
        /// <see cref="Move"/> says otherwise.
        /// <para>
        /// A link that would close a cycle is <b>refused</b> and reported (<see cref="CarrierLink.Cycle"/>): see
        /// the return values for what each outcome means.
        /// </para>
        /// </summary>
        public CarrierLink SetCarrier(ulong id, ulong carrier)
        {
            if (!_locations.TryGetValue(id, out var loc)) return CarrierLink.UnknownItem;
            if (id == carrier) return CarrierLink.Cycle;
            if (loc.Carrier == carrier) return CarrierLink.Unchanged;
            if (carrier != 0 && WouldCycle(id, carrier)) return CarrierLink.Cycle;
            if (loc.Carrier != 0) UnlinkChild(loc.Carrier, id);
            loc.Carrier = carrier;
            // Put down where it was let off: an item's own region is only meaningful once its owner recomputes it
            // from a position, and until then "where the ship left it" beats "where it boarded".
            if (carrier == 0 && loc.Own == InterestPlacement.Region && loc.Where == InterestPlacement.Region)
                loc.OwnRegion = loc.Region;
            _locations[id] = loc;
            if (carrier != 0) LinkChild(carrier, id);
            Resettle(id);
            return carrier == 0 ? CarrierLink.Detached : CarrierLink.Linked;
        }

        /// <summary>Whether putting <paramref name="id"/> inside <paramref name="carrier"/> would close a cycle.</summary>
        private bool WouldCycle(ulong id, ulong carrier)
        {
            ulong at = carrier;
            for (int guard = 0; guard <= _locations.Count; guard++)
            {
                if (at == id) return true;
                if (at == 0 || !_locations.TryGetValue(at, out var loc)) return false;
                at = loc.Carrier;
            }
            return true; // longer than the index: the chain above the proposed carrier is already a cycle
        }

        public bool Remove(ulong id)
        {
            if (!_locations.TryGetValue(id, out var loc)) return false;
            Detach(id, ref loc);
            if (loc.Carrier != 0) UnlinkChild(loc.Carrier, id);
            // Orphan the children rather than drop them: the carrier's despawn is relayed separately, and a
            // child that outlives its carrier must stay findable where it last was - unless it has a reach of its
            // own to go back to, which is exactly what getting off any other way would give it.
            if (loc.HasChildren && _children.TryGetValue(id, out var kids))
            {
                _orphans.Clear();
                _orphans.AddRange(kids);
                kids.Clear();
                _children.Remove(id);
                _idPool.Push(kids);
                _locations.Remove(id); // out of the way before the orphans resolve their new root
                for (int i = 0; i < _orphans.Count; i++)
                {
                    ulong child = _orphans[i];
                    if (!_locations.TryGetValue(child, out var childLoc)) continue;
                    childLoc.Carrier = 0;
                    if (childLoc.Own == InterestPlacement.Region && childLoc.Where == InterestPlacement.Region)
                        childLoc.OwnRegion = childLoc.Region;
                    _locations[child] = childLoc;
                    Resettle(child);
                }
                _orphans.Clear();
                return true;
            }
            _locations.Remove(id);
            return true;
        }

        public void Clear()
        {
            foreach (var pair in _regions) { pair.Value.Clear(); _entryPool.Push(pair.Value); }
            foreach (var pair in _children) { pair.Value.Clear(); _idPool.Push(pair.Value); }
            _regions.Clear();
            _children.Clear();
            _locations.Clear();
            _wide.Clear();
            _global.Clear();
            _walk.Clear();
            _orphans.Clear();
        }

        // ------------------------------------------------------------------------------------------- internals

        private List<Entry> ListOf(in Location loc) => loc.Where switch
        {
            InterestPlacement.Wide => _wide,
            InterestPlacement.Global => _global,
            _ => _regions.TryGetValue(loc.Region, out var list) ? list : null,
        };

        private T Read(in Location loc)
        {
            var list = ListOf(loc);
            return list != null && (uint)loc.Slot < (uint)list.Count ? list[loc.Slot].Value : default;
        }

        private void Write(in Location loc, ulong id, in T value)
        {
            var list = ListOf(loc);
            if (list != null && (uint)loc.Slot < (uint)list.Count) list[loc.Slot] = new Entry { Id = id, Value = value };
        }

        private void Attach(ulong id, InterestPlacement where, ulong region, in T value, ref Location loc)
        {
            List<Entry> list;
            if (where == InterestPlacement.Wide) list = _wide;
            else if (where == InterestPlacement.Global) list = _global;
            else if (!_regions.TryGetValue(region, out list))
            {
                list = _entryPool.Count > 0 ? _entryPool.Pop() : new List<Entry>(8);
                _regions[region] = list;
            }
            loc.Where = where;
            loc.Region = where == InterestPlacement.Region ? region : 0;
            loc.Slot = list.Count;
            list.Add(new Entry { Id = id, Value = value });
        }

        /// <summary>Swap-remove from the bucket, repairing the slot the moved item remembers.</summary>
        private void Detach(ulong id, ref Location loc)
        {
            var list = ListOf(loc);
            if (list == null) return;
            int slot = loc.Slot;
            if ((uint)slot >= (uint)list.Count || list[slot].Id != id) return;
            int last = list.Count - 1;
            if (slot != last)
            {
                var moved = list[last];
                list[slot] = moved;
                if (_locations.TryGetValue(moved.Id, out var movedLoc)) { movedLoc.Slot = slot; _locations[moved.Id] = movedLoc; }
            }
            list.RemoveAt(last);
            if (list.Count == 0 && loc.Where == InterestPlacement.Region)
            {
                _regions.Remove(loc.Region);
                _entryPool.Push(list);
            }
        }

        private void LinkChild(ulong carrier, ulong child)
        {
            if (!_children.TryGetValue(carrier, out var kids))
            {
                kids = _idPool.Count > 0 ? _idPool.Pop() : new List<ulong>(4);
                _children[carrier] = kids;
            }
            if (!kids.Contains(child)) kids.Add(child);
            if (_locations.TryGetValue(carrier, out var loc) && !loc.HasChildren) { loc.HasChildren = true; _locations[carrier] = loc; }
        }

        private void UnlinkChild(ulong carrier, ulong child)
        {
            if (!_children.TryGetValue(carrier, out var kids)) return;
            kids.Remove(child);
            if (kids.Count > 0) return;
            _children.Remove(carrier);
            _idPool.Push(kids);
            if (_locations.TryGetValue(carrier, out var loc) && loc.HasChildren) { loc.HasChildren = false; _locations[carrier] = loc; }
        }

        /// <summary>
        /// Put an item where its root carrier says it belongs, and everything riding in it with it. Every change
        /// that can alter where a subtree sits — a placement, a move, a link, a detach, an orphaning — ends here,
        /// so there is one answer to "where is this" and no path that moves half a tree.
        /// </summary>
        private void Resettle(ulong id)
        {
            if (!_locations.TryGetValue(id, out var loc)) return;
            Resolve(id, in loc, out var where, out ulong region);
            Settle(id, ref loc, where, region);
            if (loc.HasChildren) ResettleChildren(id, where, region);
        }

        /// <summary>Everything in the subtree shares the root's bucket; the root itself is already settled.</summary>
        private void ResettleChildren(ulong id, InterestPlacement where, ulong region)
        {
            _walk.Clear();
            int count = CollectCarried(id, _walk);
            for (int i = 0; i < count; i++)
            {
                if (!_locations.TryGetValue(_walk[i], out var loc)) continue;
                Settle(_walk[i], ref loc, where, region);
            }
            _walk.Clear();
        }

        /// <summary>Where an item belongs: its own placement, or its root carrier's while it is carried.</summary>
        private void Resolve(ulong id, in Location loc, out InterestPlacement where, out ulong region)
        {
            ulong root = loc.Carrier == 0 ? id : RootOf(id);
            if (root == id || !_locations.TryGetValue(root, out var rootLoc))
            {
                where = loc.Own;
                region = loc.OwnRegion;
                return;
            }
            where = rootLoc.Where;
            region = rootLoc.Region;
        }

        /// <summary>Move one item into a bucket, keeping its record. Nothing happens when it is already there.</summary>
        private void Settle(ulong id, ref Location loc, InterestPlacement where, ulong region)
        {
            if (where != InterestPlacement.Region) region = 0;
            if (loc.Where == where && loc.Region == region) return;
            var value = Read(loc);
            Detach(id, ref loc);
            Attach(id, where, region, value, ref loc);
            _locations[id] = loc;
        }

        /// <summary>Allocation-free view over one bucket.</summary>
        public readonly struct Bucket
        {
            private readonly List<Entry> _list;
            internal Bucket(List<Entry> list) { _list = list; }
            public int Count => _list?.Count ?? 0;
            public Entry this[int i] => _list[i];
            public Enumerator GetEnumerator() => new Enumerator(_list);

            public struct Enumerator
            {
                private readonly List<Entry> _list;
                private int _index;
                internal Enumerator(List<Entry> list) { _list = list; _index = -1; }
                public Entry Current => _list[_index];
                public bool MoveNext() => _list != null && ++_index < _list.Count;
            }
        }
    }
}
