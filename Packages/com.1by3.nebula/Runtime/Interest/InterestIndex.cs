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

    /// <summary>
    /// Region → dense list of items, with swap-remove and O(1) rebucketing. Used by the gateway for its entity
    /// records and by the worker for its authoritative entities, so both sides answer "what is near this focus"
    /// the same way.
    /// <para>
    /// Carried entities (design D3) are bucketed in their <b>root carrier's</b> region and move with it: a ship and
    /// everything riding in it enter and leave a client's set as one unit at any speed. Bucketing a passenger by
    /// its own resolved position would let a seat straddle a region edge and pop separately from its ship.
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
            public InterestPlacement Where;
            public ulong Region;
            public int Slot;
            /// <summary>Direct carrier (0 = none). The item is bucketed in the root carrier's region.</summary>
            public ulong Carrier;
            public bool HasChildren;
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

        /// <summary>The item's direct carrier, or 0 when it is not carried.</summary>
        public ulong CarrierOf(ulong id) => _locations.TryGetValue(id, out var loc) ? loc.Carrier : 0;

        // ------------------------------------------------------------------------------------------- mutation

        /// <summary>Add or update an item in a region. A carried item follows its carrier, so the region is ignored for one.</summary>
        public void Add(ulong id, ulong region, in T value) => Place(id, InterestPlacement.Region, region, value);

        /// <summary>Add or update an item in the wide list (its own relevance radius decides who hears about it).</summary>
        public void AddWide(ulong id, in T value) => Place(id, InterestPlacement.Wide, 0, value);

        /// <summary>Add or update an always-relevant item.</summary>
        public void AddGlobal(ulong id, in T value) => Place(id, InterestPlacement.Global, 0, value);

        private void Place(ulong id, InterestPlacement where, ulong region, in T value)
        {
            if (_locations.TryGetValue(id, out var loc))
            {
                if (loc.Carrier != 0 && where == InterestPlacement.Region) region = loc.Region;
                if (loc.Where == where && (where != InterestPlacement.Region || loc.Region == region))
                {
                    Write(loc, id, value);
                    return;
                }
                Detach(id, ref loc);
                Attach(id, where, region, value, ref loc);
                _locations[id] = loc;
                if (where == InterestPlacement.Region) MoveChildren(id, region);
                return;
            }
            var fresh = new Location();
            Attach(id, where, region, value, ref fresh);
            _locations[id] = fresh;
        }

        /// <summary>
        /// Rebucket an item whose region key changed. Its carried subtree moves with it. Returns false for an
        /// unknown item, and for a carried one (which is bucketed with its carrier and must not move by itself).
        /// </summary>
        public bool Move(ulong id, ulong region)
        {
            if (!_locations.TryGetValue(id, out var loc) || loc.Carrier != 0) return false;
            if (loc.Where == InterestPlacement.Region && loc.Region == region) return true;
            var value = Read(loc);
            Detach(id, ref loc);
            Attach(id, InterestPlacement.Region, region, value, ref loc);
            _locations[id] = loc;
            MoveChildren(id, region);
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
        /// Link a carried entity to the entity carrying it (0 detaches). The child is moved into the carrier's
        /// region at once, and follows it from then on. The carrier need not be in the index yet; the link is
        /// remembered either way, and the child is placed the next time the carrier moves.
        /// </summary>
        public void SetCarrier(ulong id, ulong carrier)
        {
            if (!_locations.TryGetValue(id, out var loc) || loc.Carrier == carrier || id == carrier) return;
            if (loc.Carrier != 0) UnlinkChild(loc.Carrier, id);
            loc.Carrier = carrier;
            _locations[id] = loc;
            if (carrier == 0) return;
            LinkChild(carrier, id);
            if (_locations.TryGetValue(carrier, out var parent) && parent.Where == InterestPlacement.Region)
                MoveSubtree(id, parent.Region);
        }

        public bool Remove(ulong id)
        {
            if (!_locations.TryGetValue(id, out var loc)) return false;
            Detach(id, ref loc);
            if (loc.Carrier != 0) UnlinkChild(loc.Carrier, id);
            // Orphan the children rather than drop them: the carrier's despawn is relayed separately, and a
            // child that outlives its carrier must stay findable where it last was.
            if (loc.HasChildren && _children.TryGetValue(id, out var kids))
            {
                foreach (ulong child in kids)
                    if (_locations.TryGetValue(child, out var childLoc)) { childLoc.Carrier = 0; _locations[child] = childLoc; }
                kids.Clear();
                _children.Remove(id);
                _idPool.Push(kids);
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

        private void MoveChildren(ulong carrier, ulong region)
        {
            if (!_children.TryGetValue(carrier, out var kids) || kids.Count == 0) return;
            _walk.Clear();
            _walk.AddRange(kids);
            for (int i = 0; i < _walk.Count; i++)
            {
                ulong child = _walk[i];
                if (!_locations.TryGetValue(child, out var loc)) continue;
                if (loc.Where == InterestPlacement.Region && loc.Region != region)
                {
                    var value = Read(loc);
                    Detach(child, ref loc);
                    Attach(child, InterestPlacement.Region, region, value, ref loc);
                    _locations[child] = loc;
                }
                if (loc.HasChildren && _children.TryGetValue(child, out var grandKids)) _walk.AddRange(grandKids);
            }
            _walk.Clear();
        }

        private void MoveSubtree(ulong id, ulong region)
        {
            if (_locations.TryGetValue(id, out var loc) && loc.Where == InterestPlacement.Region && loc.Region != region)
            {
                var value = Read(loc);
                Detach(id, ref loc);
                Attach(id, InterestPlacement.Region, region, value, ref loc);
                _locations[id] = loc;
            }
            MoveChildren(id, region);
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
