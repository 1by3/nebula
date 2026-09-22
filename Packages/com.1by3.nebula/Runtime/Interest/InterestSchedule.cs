using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// Which clients a gateway re-evaluates on this tick. Every client is evaluated at
    /// <see cref="InterestSettings.EvalHz"/>, but never all of them on the same tick: routine evaluations are
    /// spread over the ticks of one interval by a cursor walking a ring, at a budget derived from the time the
    /// tick actually took. A hundred clients on a 60 Hz loop at 4 Hz evaluation cost about seven evaluations a
    /// tick instead of a hundred every fifteenth tick, which is the difference between a flat frame time and a
    /// loop that stalls four times a second.
    /// <para>
    /// Clients marked dirty — a new pawn, a changed instance, a focus-mode change, a policy that says so — are
    /// not routine and jump the queue: they are collected before the routine slice, on the next tick. That is
    /// bounded too (<see cref="MinDirtyPerTick"/>, <see cref="DirtyBurst"/>) so a change that dirties every
    /// client at once is spread rather than turned into the stall this class exists to prevent, and the dirty
    /// scan has a cursor of its own so a storm cannot keep serving the same few clients.
    /// </para>
    /// Steady state allocates nothing: the ring, the slot map and the duplicate guard are reused.
    /// </summary>
    public sealed class InterestSchedule
    {
        /// <summary>However small the routine budget is, this many dirty clients are always taken in a tick.</summary>
        public const int MinDirtyPerTick = 8;
        /// <summary>A tick may take this many times the routine budget in dirty clients before the rest wait.</summary>
        public const int DirtyBurst = 4;

        private readonly List<ulong> _ring = new List<ulong>();
        private readonly Dictionary<ulong, int> _slot = new Dictionary<ulong, int>();
        /// <summary>Ids already in this tick's slice, so a dirty client is not evaluated twice by the routine pass.</summary>
        private readonly HashSet<ulong> _taken = new HashSet<ulong>();
        private int _cursor, _dirtyCursor;
        /// <summary>Fractional evaluations carried between ticks; without it a fast loop would never reach a whole one.</summary>
        private double _credit;

        /// <summary>Clients in the rotation.</summary>
        public int Count => _ring.Count;
        /// <summary>Dirty clients the last <see cref="Collect"/> took.</summary>
        public int LastDirty { get; private set; }
        /// <summary>Routine clients the last <see cref="Collect"/> took (an already-dirty one does not count twice).</summary>
        public int LastRoutine { get; private set; }

        /// <summary>Put a client in the rotation. Idempotent: welcoming a reclaimed session twice is not two clients.</summary>
        public bool Add(ulong clientId)
        {
            if (clientId == 0 || _slot.ContainsKey(clientId)) return false;
            _slot[clientId] = _ring.Count;
            _ring.Add(clientId);
            return true;
        }

        /// <summary>Take a client out of the rotation. The last entry fills the hole, so removal is O(1).</summary>
        public bool Remove(ulong clientId)
        {
            if (!_slot.TryGetValue(clientId, out int index)) return false;
            int last = _ring.Count - 1;
            ulong moved = _ring[last];
            if (moved != clientId)
            {
                _ring[index] = moved;
                _slot[moved] = index;
            }
            _ring.RemoveAt(last);
            _slot.Remove(clientId);
            if (_cursor > last) _cursor = 0;
            if (_dirtyCursor > last) _dirtyCursor = 0;
            return true;
        }

        public bool Contains(ulong clientId) => _slot.ContainsKey(clientId);

        public void Clear()
        {
            _ring.Clear();
            _slot.Clear();
            _taken.Clear();
            _cursor = _dirtyCursor = 0;
            _credit = 0;
            LastDirty = LastRoutine = 0;
        }

        /// <summary>
        /// Fill <paramref name="due"/> with the clients to evaluate on this tick: the dirty ones first, then the
        /// routine slice, with no id twice. <paramref name="dt"/> is the seconds since the last call, which is
        /// what makes the rate independent of the loop's tick rate and honest when a tick runs long.
        /// </summary>
        public void Collect(double dt, float evalHz, Func<ulong, bool> isDirty, List<ulong> due)
        {
            due.Clear();
            _taken.Clear();
            LastDirty = LastRoutine = 0;
            int n = _ring.Count;
            if (n == 0) { _credit = 0; return; }

            double interval = evalHz > 0 ? 1.0 / evalHz : InterestSettings.Default.EvalInterval;
            if (dt > 0) _credit += n * dt / interval;
            // A stalled loop (a breakpoint, a long GC) must not come back and evaluate everybody several times
            // over; one whole sweep of arrears is as much as is ever useful.
            if (_credit > n) _credit = n;
            int routine = (int)_credit;
            _credit -= routine;

            if (isDirty != null)
            {
                int cap = Math.Max(MinDirtyPerTick, routine * DirtyBurst);
                if (cap > n) cap = n;
                for (int scanned = 0; scanned < n && LastDirty < cap; scanned++)
                {
                    if (_dirtyCursor >= n) _dirtyCursor = 0;
                    ulong id = _ring[_dirtyCursor++];
                    if (!isDirty(id) || !_taken.Add(id)) continue;
                    due.Add(id);
                    LastDirty++;
                }
            }

            for (int i = 0; i < routine; i++)
            {
                if (_cursor >= n) _cursor = 0;
                ulong id = _ring[_cursor++];
                // Already taken by the dirty pass: it has had its turn, and the slot is spent either way.
                if (!_taken.Add(id)) continue;
                due.Add(id);
                LastRoutine++;
            }
        }
    }
}
