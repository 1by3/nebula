using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// The shared arithmetic of the region-subscription protocol (design §5): a set's fingerprint. The hash is a
    /// XOR of a strong per-region mix, so it does not depend on the order regions were added in — which is the
    /// point, since one end keeps a hash set and the other rebuilds it from deltas.
    /// </summary>
    public static class RegionSubscription
    {
        /// <summary>SplitMix64 finalizer: spreads the packed grid coordinate, whose low bits move in lock-step.</summary>
        public static ulong Mix(ulong region)
        {
            ulong z = region + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Fingerprint of a whole set, for an audit or a test.</summary>
        public static ulong Hash(IEnumerable<ulong> regions)
        {
            ulong hash = 0;
            if (regions != null) foreach (ulong region in regions) hash ^= Mix(region);
            return hash;
        }
    }

    /// <summary>
    /// The gateway's end of one worker link's subscription: what this gateway wants from that worker, and the
    /// messages that get the worker there. Callers describe the regions they need each pass and this decides
    /// what to send — a delta while the worker is in step, a Full snapshot after a resync, a reconnect, a worker
    /// incarnation change, or every <see cref="InterestSettings.ResyncSeconds"/> as an audit.
    /// <para>
    /// Unsubscribing is delayed by <see cref="LingerSeconds"/> so a client pacing along a region edge does not
    /// make the worker start and stop sending the same entities. The clock is injected because the tests, the
    /// standalone gateway and the Unity gateway all measure time differently.
    /// </para>
    /// </summary>
    public sealed class RegionSubscriptionSender
    {
        private readonly Func<double> _clock;
        private readonly HashSet<ulong> _desired = new HashSet<ulong>();
        private readonly HashSet<ulong> _needed = new HashSet<ulong>();
        private readonly HashSet<ulong> _sent = new HashSet<ulong>();
        private readonly Dictionary<ulong, double> _linger = new Dictionary<ulong, double>();
        private readonly List<ulong> _expired = new List<ulong>();
        private readonly List<ulong> _add = new List<ulong>();
        private readonly List<ulong> _remove = new List<ulong>();
        private readonly List<ulong> _foci = new List<ulong>();
        private readonly List<ulong> _entities = new List<ulong>();
        private bool _sideChannelDirty = true;
        private bool _forceFull = true;
        private uint _seq;
        private double _lastFull = double.NegativeInfinity;

        public RegionSubscriptionSender(Func<double> clock = null) { _clock = clock; }

        /// <summary>Seconds a region stays subscribed after the last pass that needed it.</summary>
        public double LingerSeconds { get; set; } = InterestSettings.Default.RegionLingerSeconds;
        /// <summary>Seconds between belt-and-braces Full snapshots. 0 or less turns the audit off.</summary>
        public double ResyncSeconds { get; set; } = InterestSettings.Default.ResyncSeconds;
        /// <summary>Most region ids in one message; a larger change is chunked and only the last chunk commits.</summary>
        public int MaxRegionsPerMessage { get; set; } = 512;
        /// <summary>The grid these ids were made with; it travels with every message so the worker can refuse a mismatch.</summary>
        public InterestGrid Grid { get; set; }
        /// <summary>The sequence the worker is believed to hold.</summary>
        public uint Seq => _seq;
        /// <summary>Regions currently subscribed, including ones only still there because of the linger.</summary>
        public int Count => _desired.Count;
        public bool Contains(ulong region) => _desired.Contains(region);
        public HashSet<ulong>.Enumerator GetEnumerator() => _desired.GetEnumerator();

        private double Now => _clock?.Invoke() ?? 0;

        /// <summary>Start describing the regions this gateway needs. Every <see cref="Need"/> until <see cref="EndPass"/> forms one set.</summary>
        public void BeginPass() => _needed.Clear();

        public void Need(ulong region) => _needed.Add(region);

        public void Need(List<ulong> regions)
        {
            if (regions == null) return;
            for (int i = 0; i < regions.Count; i++) _needed.Add(regions[i]);
        }

        /// <summary>
        /// Adopt the pass: newly needed regions are subscribed at once, and regions that dropped out are kept for
        /// <see cref="LingerSeconds"/> before they go.
        /// </summary>
        public void EndPass() => EndPass(Now);

        public void EndPass(double now)
        {
            foreach (ulong region in _needed) { _desired.Add(region); _linger.Remove(region); }
            _expired.Clear();
            foreach (ulong region in _desired)
            {
                if (_needed.Contains(region)) continue;
                if (!_linger.TryGetValue(region, out double deadline)) _linger[region] = now + LingerSeconds;
                else if (now >= deadline) _expired.Add(region);
            }
            foreach (ulong region in _expired) { _desired.Remove(region); _linger.Remove(region); }
            _expired.Clear();
        }

        /// <summary>Subscribe a region outside a pass (an explicit container, a pinned interior).</summary>
        public void Add(ulong region) { _desired.Add(region); _linger.Remove(region); }

        /// <summary>Drop a region now, skipping the linger (the last client in it disconnected).</summary>
        public void Drop(ulong region) { _desired.Remove(region); _linger.Remove(region); }

        /// <summary>The focus regions the worker matches wide entities against. Kept in full; the list is small.</summary>
        public void SetFoci(List<ulong> fociRegions) => Replace(_foci, fociRegions);

        /// <summary>Explicit per-entity subscriptions (policy extras). Kept in full.</summary>
        public void SetEntities(List<ulong> netIds) => Replace(_entities, netIds);

        private void Replace(List<ulong> target, List<ulong> source)
        {
            if (Same(target, source)) return;
            target.Clear();
            if (source != null) target.AddRange(source);
            _sideChannelDirty = true;
        }

        private static bool Same(List<ulong> a, List<ulong> b)
        {
            int n = b?.Count ?? 0;
            if (a.Count != n) return false;
            for (int i = 0; i < n; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>The worker could not apply what we sent: the next build is a Full snapshot.</summary>
        public void OnResync(uint haveSeq) => _forceFull = true;

        /// <summary>A new link, or the worker restarted: forget what it was believed to hold.</summary>
        public void Reset()
        {
            _sent.Clear();
            _forceFull = true;
            _sideChannelDirty = true;
        }

        /// <summary>Forget everything, including what we want (the gateway is done with this worker).</summary>
        public void Clear()
        {
            _desired.Clear();
            _needed.Clear();
            _linger.Clear();
            Reset();
        }

        /// <summary>
        /// Produce the messages that bring the worker to the current set, appended to <paramref name="into"/>, and
        /// return whether anything is to be sent. The messages must go out in order on the reliable channel.
        /// </summary>
        public bool Build(List<InterestSubscribeMsg> into) => Build(into, Now);

        public bool Build(List<InterestSubscribeMsg> into, double now)
        {
            if (into == null) return false;
            bool full = _forceFull || (ResyncSeconds > 0 && now - _lastFull >= ResyncSeconds);
            _add.Clear();
            _remove.Clear();
            if (full)
            {
                foreach (ulong region in _desired) _add.Add(region);
            }
            else
            {
                foreach (ulong region in _desired) if (!_sent.Contains(region)) _add.Add(region);
                foreach (ulong region in _sent) if (!_desired.Contains(region)) _remove.Add(region);
                if (_add.Count == 0 && _remove.Count == 0 && !_sideChannelDirty) return false;
            }

            _seq++;
            int chunk = Math.Max(1, MaxRegionsPerMessage);
            int total = _add.Count + _remove.Count;
            int messages = Math.Max(1, (total + chunk - 1) / chunk);
            uint baseSeq = _seq - 1;
            ulong hash = RegionSubscription.Hash(_desired);
            int addAt = 0, removeAt = 0;
            for (int i = 0; i < messages; i++)
            {
                bool last = i == messages - 1;
                var msg = new InterestSubscribeMsg
                {
                    Seq = _seq,
                    BaseSeq = baseSeq,
                    Grid = Grid,
                    Flags = (full ? InterestSubscribeFlags.Full : InterestSubscribeFlags.None) | (last ? InterestSubscribeFlags.Commit : InterestSubscribeFlags.None),
                    Add = new List<ulong>(),
                    Remove = new List<ulong>(),
                    FociRegions = last ? new List<ulong>(_foci) : new List<ulong>(),
                    Entities = last ? new List<ulong>(_entities) : new List<ulong>(),
                    SetCount = (uint)_desired.Count,
                    SetHash = hash,
                };
                int room = chunk;
                while (room > 0 && addAt < _add.Count) { msg.Add.Add(_add[addAt++]); room--; }
                while (room > 0 && removeAt < _remove.Count) { msg.Remove.Add(_remove[removeAt++]); room--; }
                into.Add(msg);
            }

            _sent.Clear();
            foreach (ulong region in _desired) _sent.Add(region);
            _forceFull = false;
            _sideChannelDirty = false;
            if (full) _lastFull = now;
            return true;
        }
    }

    /// <summary>
    /// The worker's end of one gateway link's subscription: the region set it filters that gateway's traffic
    /// with. Everything about applying a message is defensive, because a set that quietly drifts apart from the
    /// gateway's would show up as entities that never spawn rather than as an error: a delta applies only against
    /// the sequence it was built on, the result must match the sender's (count, hash), and anything else keeps
    /// the old set and asks for a Full snapshot.
    /// </summary>
    public sealed class RegionSubscriptionReceiver
    {
        private readonly HashSet<ulong> _regions = new HashSet<ulong>();
        private readonly List<ulong> _journalAdded = new List<ulong>();
        private readonly List<ulong> _journalRemoved = new List<ulong>();
        private readonly List<ulong> _lastAdded = new List<ulong>();
        private readonly List<ulong> _lastRemoved = new List<ulong>();
        private readonly List<ulong> _foci = new List<ulong>();
        private readonly List<ulong> _entities = new List<ulong>();
        private readonly List<ulong> _scratch = new List<ulong>();
        private ulong _hash;
        private uint _seq;
        private bool _staging;
        private uint _stagingSeq;

        /// <summary>
        /// The grid this worker made its own region ids with. When it is valid, a message built with a different
        /// grid is rejected (design D2): filtering with ids that mean something else would silently hide entities.
        /// Leave it default to adopt the sender's.
        /// </summary>
        public InterestGrid Grid { get; set; }

        /// <summary>The sequence of the set currently held. Sent back in <see cref="InterestResyncMsg"/>.</summary>
        public uint Seq => _seq;
        public int Count => _regions.Count;
        public ulong Hash => _hash;
        /// <summary>True while a chunked update is part-applied; the set is still the one the gateway last committed.</summary>
        public bool IsStaging => _staging;
        public bool Contains(ulong region) => _regions.Contains(region);
        public HashSet<ulong>.Enumerator GetEnumerator() => _regions.GetEnumerator();
        /// <summary>Regions added by the last committed apply; a worker sends spawns for the entities in these.</summary>
        public IReadOnlyList<ulong> Added => _lastAdded;
        /// <summary>Regions removed by the last committed apply. Nothing is sent for them: the gateway drops its own cache.</summary>
        public IReadOnlyList<ulong> Removed => _lastRemoved;
        /// <summary>The gateway's focus regions, for matching wide entities.</summary>
        public IReadOnlyList<ulong> FociRegions => _foci;
        /// <summary>Entity ids this gateway subscribed explicitly.</summary>
        public IReadOnlyList<ulong> Entities => _entities;
        /// <summary>Raised after every committed apply, once <see cref="Added"/> and <see cref="Removed"/> describe it.</summary>
        public event Action<RegionSubscriptionReceiver> Changed;

        /// <summary>Forget the set (the link dropped, or the gateway restarted). The next Full snapshot rebuilds it.</summary>
        public void Reset()
        {
            _regions.Clear();
            _journalAdded.Clear();
            _journalRemoved.Clear();
            _lastAdded.Clear();
            _lastRemoved.Clear();
            _foci.Clear();
            _entities.Clear();
            _hash = 0;
            _seq = 0;
            _staging = false;
        }

        /// <summary>
        /// Apply one message. Returns false when the worker must answer <see cref="InterestResyncMsg"/> with
        /// <see cref="Seq"/>; <paramref name="rejection"/> then says why, which is worth logging loudly.
        /// </summary>
        public bool Apply(in InterestSubscribeMsg msg, out string rejection)
        {
            rejection = null;
            var senderGrid = msg.Grid;
            if (Grid.IsValid)
            {
                if (!Grid.Matches(senderGrid))
                {
                    Rollback();
                    rejection = $"interest grid mismatch: this worker uses {Grid}, the gateway sent {senderGrid}";
                    return false;
                }
            }
            else if (senderGrid.IsValid) Grid = senderGrid;

            // A resend of the update we already hold: the channel is reliable, but a gateway that restarts its
            // build after a resync can repeat one. Accept it when it describes exactly the set we have.
            if (!_staging && msg.Seq == _seq && _seq != 0)
            {
                if (msg.SetCount == (uint)_regions.Count && msg.SetHash == _hash) return true;
                Rollback();
                rejection = $"duplicate subscription seq {msg.Seq} describes a different set ({msg.SetCount}/{msg.SetHash:x16} vs {_regions.Count}/{_hash:x16})";
                return false;
            }

            if (!_staging || _stagingSeq != msg.Seq)
            {
                Rollback();
                if (!msg.IsFull && msg.BaseSeq != _seq)
                {
                    rejection = $"subscription delta is based on seq {msg.BaseSeq}, this worker holds {_seq}";
                    return false;
                }
                _staging = true;
                _stagingSeq = msg.Seq;
                if (msg.IsFull) RemoveAll();
            }

            if (msg.Remove != null) for (int i = 0; i < msg.Remove.Count; i++) RemoveRegion(msg.Remove[i]);
            if (msg.Add != null) for (int i = 0; i < msg.Add.Count; i++) AddRegion(msg.Add[i]);
            if (!msg.IsCommit) return true;

            if (msg.SetCount != (uint)_regions.Count || msg.SetHash != _hash)
            {
                string got = $"{_regions.Count}/{_hash:x16}";
                Rollback();
                rejection = $"subscription seq {msg.Seq} did not verify: the gateway expected {msg.SetCount}/{msg.SetHash:x16}, applying gave {got}";
                return false;
            }

            _lastAdded.Clear(); _lastAdded.AddRange(_journalAdded);
            _lastRemoved.Clear(); _lastRemoved.AddRange(_journalRemoved);
            _journalAdded.Clear();
            _journalRemoved.Clear();
            _staging = false;
            _seq = msg.Seq;
            CopyInto(_foci, msg.FociRegions);
            CopyInto(_entities, msg.Entities);
            Changed?.Invoke(this);
            return true;
        }

        private static void CopyInto(List<ulong> target, List<ulong> source)
        {
            target.Clear();
            if (source != null) target.AddRange(source);
        }

        private void AddRegion(ulong region)
        {
            if (!_regions.Add(region)) return;
            _hash ^= RegionSubscription.Mix(region);
            if (!_journalRemoved.Remove(region)) _journalAdded.Add(region);
        }

        private void RemoveRegion(ulong region)
        {
            if (!_regions.Remove(region)) return;
            _hash ^= RegionSubscription.Mix(region);
            if (!_journalAdded.Remove(region)) _journalRemoved.Add(region);
        }

        private void RemoveAll()
        {
            _scratch.Clear();
            foreach (ulong region in _regions) _scratch.Add(region);
            for (int i = 0; i < _scratch.Count; i++) RemoveRegion(_scratch[i]);
            _scratch.Clear();
        }

        /// <summary>Undo everything staged since the last commit, so a failed chain leaves the old set untouched.</summary>
        private void Rollback()
        {
            if (!_staging && _journalAdded.Count == 0 && _journalRemoved.Count == 0) return;
            for (int i = 0; i < _journalAdded.Count; i++)
                if (_regions.Remove(_journalAdded[i])) _hash ^= RegionSubscription.Mix(_journalAdded[i]);
            for (int i = 0; i < _journalRemoved.Count; i++)
                if (_regions.Add(_journalRemoved[i])) _hash ^= RegionSubscription.Mix(_journalRemoved[i]);
            _journalAdded.Clear();
            _journalRemoved.Clear();
            _staging = false;
        }
    }
}
