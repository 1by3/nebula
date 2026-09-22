using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// What <see cref="ClientInterest{T,TSource}"/> needs to know about the records it is evaluating. The gateway
    /// implements this over its own entity record and the worker over its authoritative entities, so neither has
    /// to copy its state into a shape interest management likes.
    /// <para>
    /// Implement it on a <b>struct</b>: the type parameter is constrained to one so the calls are direct and an
    /// evaluation allocates nothing, however many entities it walks.
    /// </para>
    /// </summary>
    public interface IInterestSource<T>
    {
        /// <summary>Fill in the interest facts about one record: absolute position, relevance radius, flags, carrier.</summary>
        void Describe(ulong id, in T value, out InterestEntity entity);

        /// <summary>
        /// The security filter: instance rules (an unknown container is never observable), then the game policy.
        /// A false here removes the entity from the set immediately, without the leave hysteresis.
        /// </summary>
        bool Authorize(in InterestClient client, in InterestEntity entity);
    }

    /// <summary>
    /// One client's interest set and the state machine that maintains it. Evaluation walks only the
    /// regions the client's foci cover, so its cost follows what is near the client rather than the size of the
    /// world, and the set is stable: an entity enters at the relevance radius and leaves only once it has been
    /// more than <see cref="InterestSettings.ExitMargin"/> further out for
    /// <see cref="InterestSettings.LingerSeconds"/>. That hysteresis is what stops an entity walking a boundary
    /// from spawning and despawning several times a second on every nearby client.
    /// <para>
    /// Enter and leave events come out in an order the client can act on: carriers before their contents on the
    /// way in (a passenger's spawn names its ship's container) and contents before carriers on the way out.
    /// </para>
    /// </summary>
    public sealed class ClientInterest<T, TSource> where TSource : struct, IInterestSource<T>
    {
        /// <summary>Per-entity leave state. Public only because <see cref="Ids"/> hands out the dictionary's keys.</summary>
        public struct Member
        {
            /// <summary>When the entity first went beyond the exit radius; the linger is measured from it.</summary>
            public double OutsideSince;
            public bool Outside;
        }

        private readonly TSource _source;
        private readonly Dictionary<ulong, Member> _members = new Dictionary<ulong, Member>();
        private readonly List<InterestFocus> _foci = new List<InterestFocus>();
        private readonly List<ulong> _always = new List<ulong>();
        private readonly List<ulong> _regions = new List<ulong>();
        private readonly HashSet<ulong> _scanned = new HashSet<ulong>();
        private readonly HashSet<ulong> _visited = new HashSet<ulong>();
        private readonly List<ulong> _stale = new List<ulong>();
        private readonly List<int> _enterDepth = new List<int>();
        private readonly List<int> _leaveDepth = new List<int>();

        /// <summary>Who this set belongs to; handed to the policy's authorization on every test.</summary>
        public InterestClient Client;
        public InterestSettings Settings = InterestSettings.Default;
        /// <summary>The grid the region scan uses. Must be the grid the index was bucketed with.</summary>
        public InterestGrid Grid = InterestGrid.Resolve(InterestSettings.Default);
        /// <summary>Where the evaluation gets "now" from; tests and the standalone gateway supply their own.</summary>
        public Func<double> Clock;

        public ClientInterest(TSource source) { _source = source; }

        public int Count => _members.Count;
        public bool Contains(ulong netId) => _members.ContainsKey(netId);
        public Dictionary<ulong, Member>.KeyCollection Ids => _members.Keys;
        /// <summary>Regions the last evaluation scanned; the gateway subscribes exactly these (plus its margin).</summary>
        public int ScannedRegions => _scanned.Count;
        public IReadOnlyList<InterestFocus> Foci => _foci;

        // ------------------------------------------------------------------------------------------- inputs

        public void SetFoci(IReadOnlyList<InterestFocus> foci)
        {
            _foci.Clear();
            if (foci == null) return;
            int max = Math.Max(1, Settings.MaxFoci);
            for (int i = 0; i < foci.Count && _foci.Count < max; i++) _foci.Add(foci[i]);
        }

        /// <summary>
        /// Entities that are in the set whatever the distance: the client's own pawn, what it owns, and the
        /// policy's explicit extras. They are still authorized — an extra is not a way around a security filter.
        /// </summary>
        public void SetAlways(IReadOnlyList<ulong> ids)
        {
            _always.Clear();
            if (ids == null) return;
            for (int i = 0; i < ids.Count; i++) if (ids[i] != 0 && !_always.Contains(ids[i])) _always.Add(ids[i]);
        }

        public void AddAlways(ulong netId) { if (netId != 0 && !_always.Contains(netId)) _always.Add(netId); }
        public void RemoveAlways(ulong netId) => _always.Remove(netId);

        /// <summary>Forget the whole set without emitting leaves (the client is gone).</summary>
        public void Clear()
        {
            _members.Clear();
            _foci.Clear();
            _always.Clear();
            _scanned.Clear();
        }

        /// <summary>
        /// Take an entity out of the set now: it was despawned, its worker died, or the gateway forgot it.
        /// Appends to <paramref name="left"/> when the client had it.
        /// </summary>
        public bool Remove(ulong netId, List<ulong> left)
        {
            if (!_members.Remove(netId)) return false;
            left?.Add(netId);
            return true;
        }

        /// <summary>
        /// Re-run authorization over the set exactly as it stands and drop whatever no longer passes, appending
        /// the losses to <paramref name="left"/>. Nothing is added: this is the <b>revocation</b>
        /// half of an evaluation, for the moment something the security filter depends on was tightened — a new
        /// policy, a changed team, a fog sweep — and the answer must not wait for the client's turn in the
        /// rotation.
        /// <para>
        /// It costs one <see cref="IInterestSource{T}.Authorize"/> per entity the client already holds and makes
        /// no grid query at all, so it is strictly cheaper than the evaluation it precedes. The additive half —
        /// what the change newly <i>reveals</i> — is left to that evaluation, because revealing late is a
        /// latency bug and revoking late is a security one.
        /// </para>
        /// </summary>
        public void Revalidate(InterestIndex<T> index, List<ulong> left)
        {
            if (index == null || _members.Count == 0) return;
            int leaveFrom = left?.Count ?? 0;
            _leaveDepth.Clear();
            _stale.Clear();
            foreach (var pair in _members) _stale.Add(pair.Key);
            for (int i = 0; i < _stale.Count; i++)
            {
                ulong id = _stale[i];
                // Gone from the index entirely: nothing will ever announce it again, so it goes now rather than
                // at the next evaluation, exactly as Evaluate treats it.
                if (!index.TryGetValue(id, out var value)) { Leave(index, id, left); continue; }
                _source.Describe(id, value, out var entity);
                if (!_source.Authorize(Client, entity)) Leave(index, id, left);
            }
            _stale.Clear();
            // Contents before their carriers, the same order a leave takes anywhere else.
            SortByDepth(left, _leaveDepth, leaveFrom, false);
        }

        // ------------------------------------------------------------------------------------------- evaluation

        public void Evaluate(InterestIndex<T> index, List<ulong> entered, List<ulong> left) =>
            Evaluate(index, Clock?.Invoke() ?? 0, entered, left);

        /// <summary>
        /// Bring the set up to date against the index and append what changed. Both lists may be null when the
        /// caller only wants the set maintained (a re-evaluation after a policy change, say).
        /// </summary>
        public void Evaluate(InterestIndex<T> index, double now, List<ulong> entered, List<ulong> left)
        {
            if (index == null) return;
            int enterFrom = entered?.Count ?? 0;
            int leaveFrom = left?.Count ?? 0;
            _visited.Clear();
            _enterDepth.Clear();
            _leaveDepth.Clear();

            // Always relevant to everybody, and not distance tested at all.
            foreach (var entry in index.Global) Consider(index, entry.Id, entry.Value, true, now, entered, left);

            // The client's own always-set: pawn, owned entities, policy extras.
            for (int i = 0; i < _always.Count; i++)
            {
                ulong id = _always[i];
                if (_visited.Contains(id)) continue;
                if (index.TryGetValue(id, out var value)) Consider(index, id, value, true, now, entered, left);
            }

            // Wide entities reach further than any client's scan, so they are tested directly against the foci
            // instead of making every client widen its scan to the largest radius in the world.
            foreach (var entry in index.Wide)
            {
                if (_visited.Contains(entry.Id)) continue;
                Consider(index, entry.Id, entry.Value, false, now, entered, left);
            }

            // The regions the foci cover, at the exit radius so an entity on its way out is still seen and its
            // linger clock keeps running.
            _scanned.Clear();
            for (int i = 0; i < _foci.Count; i++)
            {
                var focus = _foci[i];
                double reach = focus.Scaled(Settings.ExitRadius);
                _regions.Clear();
                if (focus.IsBox)
                    Grid.CollectBox(focus.X - focus.HalfX - reach, focus.Y - focus.HalfY - reach, focus.Z - focus.HalfZ - reach,
                        focus.X + focus.HalfX + reach, focus.Y + focus.HalfY + reach, focus.Z + focus.HalfZ + reach, _regions);
                else Grid.CollectDisc(focus.X, focus.Y, focus.Z, reach, _regions);
                for (int r = 0; r < _regions.Count; r++)
                {
                    if (!_scanned.Add(_regions[r])) continue;
                    foreach (var entry in index.Region(_regions[r]))
                    {
                        if (_visited.Contains(entry.Id)) continue;
                        Consider(index, entry.Id, entry.Value, false, now, entered, left);
                    }
                }
            }

            // Members the scan did not reach: either they moved out of every scanned region (test them where
            // they are now) or they are gone from the index entirely (leave at once, nothing will announce them).
            _stale.Clear();
            foreach (var pair in _members) if (!_visited.Contains(pair.Key)) _stale.Add(pair.Key);
            for (int i = 0; i < _stale.Count; i++)
            {
                ulong id = _stale[i];
                if (index.TryGetValue(id, out var value)) Consider(index, id, value, false, now, entered, left);
                else Leave(index, id, left);
            }
            _stale.Clear();

            // Carriers before their contents on the way in, contents before their carriers on the way out.
            SortByDepth(entered, _enterDepth, enterFrom, true);
            SortByDepth(left, _leaveDepth, leaveFrom, false);
        }

        /// <summary>
        /// Test one entity against this client and nothing else. This is what an <i>arrival</i> costs: an entity
        /// that spawns or rebuckets into a region is tested against the clients whose focus regions cover that
        /// region, instead of waiting for their next full evaluation (which would show it up to an eval interval
        /// late) or re-scanning every client (which would be the O(clients x entities) loop interest replaces).
        /// </summary>
        public void ConsiderOne(InterestIndex<T> index, ulong id, double now, List<ulong> entered, List<ulong> left)
        {
            if (index == null || !index.TryGetValue(id, out var value)) return;
            int enterFrom = entered?.Count ?? 0;
            int leaveFrom = left?.Count ?? 0;
            _enterDepth.Clear();
            _leaveDepth.Clear();
            Consider(index, id, value, _always.Contains(id), now, entered, left);
            SortByDepth(entered, _enterDepth, enterFrom, true);
            SortByDepth(left, _leaveDepth, leaveFrom, false);
        }

        private void Consider(InterestIndex<T> index, ulong id, in T value, bool forcedInside, double now, List<ulong> entered, List<ulong> left)
        {
            _visited.Add(id);
            _source.Describe(id, value, out var entity);
            bool member = _members.TryGetValue(id, out var state);
            if (!_source.Authorize(Client, entity))
            {
                // A security boundary, not a distance: no hysteresis, no linger.
                if (member) Leave(index, id, left);
                return;
            }
            if (forcedInside || entity.AlwaysRelevant)
            {
                if (!member) Enter(index, id, entered);
                else if (state.Outside) { state.Outside = false; _members[id] = state; }
                return;
            }

            // A prefab override is always capped: InterestSettings.Validate keeps MaxRadius at or above
            // Radius, so there is no "uncapped" setting for a prefab to reach through.
            double radius = entity.RelevanceRadius <= 0 ? Settings.Radius : Math.Min(entity.RelevanceRadius, Settings.MaxRadius);
            bool inside = false, beyondExit = true;
            for (int i = 0; i < _foci.Count && !inside; i++)
            {
                var focus = _foci[i];
                double d2 = focus.SqrDistanceTo(entity.X, entity.Y, entity.Z);
                double enterAt = focus.Scaled(radius);
                double exitAt = focus.Scaled(radius + Settings.ExitMargin);
                if (d2 <= enterAt * enterAt) { inside = true; beyondExit = false; }
                else if (d2 <= exitAt * exitAt) beyondExit = false;
            }

            if (inside)
            {
                if (!member) Enter(index, id, entered);
                else if (state.Outside) { state.Outside = false; _members[id] = state; }
                return;
            }
            if (!member) return;
            if (!beyondExit)
            {
                // Inside the hysteresis band: keep it, and forget any linger it had started.
                if (state.Outside) { state.Outside = false; _members[id] = state; }
                return;
            }
            if (!state.Outside) { _members[id] = new Member { Outside = true, OutsideSince = now }; return; }
            if (now - state.OutsideSince >= Settings.LingerSeconds) Leave(index, id, left);
        }

        private void Enter(InterestIndex<T> index, ulong id, List<ulong> entered)
        {
            _members[id] = default;
            if (entered == null) return;
            entered.Add(id);
            _enterDepth.Add(CarrierDepth(index, id));
        }

        private void Leave(InterestIndex<T> index, ulong id, List<ulong> left)
        {
            _members.Remove(id);
            if (left == null) return;
            left.Add(id);
            _leaveDepth.Add(CarrierDepth(index, id));
        }

        /// <summary>
        /// How deep the entity rides: 0 for something standing in the world, 1 inside a ship, and so on. The
        /// index answers it with no cap of its own. A depth clamped to a constant would make two
        /// entities at different depths compare equal and let a spawn arrive before its container.
        /// </summary>
        private static int CarrierDepth(InterestIndex<T> index, ulong id) => index.DepthOf(id);

        /// <summary>
        /// Insertion sort of the tail this evaluation appended, by carrier depth. The lists are short (what one
        /// client gained or lost in a quarter of a second), and an insertion sort is stable and allocates nothing.
        /// </summary>
        private static void SortByDepth(List<ulong> ids, List<int> depths, int from, bool shallowFirst)
        {
            if (ids == null || depths.Count < 2) return;
            for (int i = 1; i < depths.Count; i++)
            {
                ulong id = ids[from + i];
                int depth = depths[i];
                int j = i - 1;
                while (j >= 0 && (shallowFirst ? depths[j] > depth : depths[j] < depth))
                {
                    ids[from + j + 1] = ids[from + j];
                    depths[j + 1] = depths[j];
                    j--;
                }
                ids[from + j + 1] = id;
                depths[j + 1] = depth;
            }
        }
    }
}
