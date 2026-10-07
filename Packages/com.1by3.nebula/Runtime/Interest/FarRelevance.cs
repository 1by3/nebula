using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// The far relevance tier's arithmetic (<c>docs/interest-management.md</c> §16, NEB-388), shared by the worker, the
    /// gateway and the tests. An entity that declares a far radius (<see cref="NetworkIdentity.FarRelevanceRadius"/>) is
    /// seen beyond its normal radius, out to the far radius, as a pose at its far rate and nothing else.
    /// </summary>
    public static class FarRelevance
    {
        /// <summary>The fewest and most far updates a second an entity may ask for.</summary>
        public const float MinRate = 0.1f, MaxRate = 30f;

        /// <summary>A far update rate clamped to <see cref="MinRate"/>..<see cref="MaxRate"/> (1 for nonsense).</summary>
        public static float ClampRate(float rate) => float.IsNaN(rate) ? 1f : rate < MinRate ? MinRate : rate > MaxRate ? MaxRate : rate;

        /// <summary>
        /// The hysteresis of the far tier: an entity in a receiver's far tier stays until it is this much further than
        /// its far radius. A twentieth of the radius, and never less than the mesh's exit margin, so a ship at 50 km
        /// has a 2.5 km band and a marker at the edge does not flicker four times a second.
        /// </summary>
        public static double StayMargin(double radius, double exitMargin) => Math.Max(exitMargin, radius * 0.05);

        /// <summary>
        /// Whether an entry is inside a far radius of a point, with the tier's hysteresis when the receiver already holds
        /// it (<paramref name="held"/>). Squared distance in, so a caller measuring several foci takes the least.
        /// </summary>
        public static bool Within(double sqrDistance, double radius, bool held, double exitMargin)
        {
            double reach = held ? radius + StayMargin(radius, exitMargin) : radius;
            return sqrDistance <= reach * reach;
        }
    }

    /// <summary>
    /// The worker's half of the far tier: which gateways hear about each far entity, and when. Pure bookkeeping over a
    /// <see cref="RegionPublisher"/>'s foci, so the Unity worker and the service tests' fake worker publish it the same way.
    /// <para>
    /// Each tick the host calls <see cref="Begin"/>, then <see cref="Offer"/> for every far entity it is authoritative for
    /// whose <see cref="IsDue"/> says so (with the entry filled in: its pose absolute in its scope), then <see cref="End"/>.
    /// What to send is in <see cref="Out"/>: one mask and one entry each, to be grouped by mask by the host.
    /// </para>
    /// <para>
    /// The match runs at <see cref="EvalInterval"/>: a far entity reaches a gateway with a focus of the entity's scope's
    /// own space (frame key 0) within its far radius. Every client with a pawn has one: its pawn's focus in the scope, or
    /// the enclosing focus the gateway adds around a pawn on a planet (<c>docs/container-tree.md</c> D18), so the tier
    /// crosses frames without converting foci on the worker. A gateway that gains the entity is sent an entry at once;
    /// one that loses it is sent <see cref="FarEntryKind.Gone"/>. In between, each entity is sent at its own rate.
    /// </para>
    /// </summary>
    public sealed class FarPublisher
    {
        /// <summary>One send: the gateways (<see cref="RegionPublisher"/> bits) and what they are told.</summary>
        public struct Outgoing
        {
            public ulong Mask;
            public FarEntityEntry Entry;
        }

        private struct Track
        {
            public ulong Mask;
            public double NextAt;
            public uint Epoch;
            public bool Offered;
        }

        private readonly Dictionary<ulong, Track> _tracks = new Dictionary<ulong, Track>();
        private readonly List<ulong> _scratch = new List<ulong>();
        private double _now, _nextEval;
        private bool _evaluate;

        /// <summary>Sends decided this tick, in order (a <see cref="FarEntryKind.Gone"/> before the next state of anything).</summary>
        public readonly List<Outgoing> Out = new List<Outgoing>();
        /// <summary>Seconds between two matches of the far list against the gateways' foci (the interest evaluation rate).</summary>
        public double EvalInterval = 0.25;
        /// <summary>The mesh's exit margin, the floor of the tier's hysteresis (<see cref="FarRelevance.StayMargin"/>).</summary>
        public double ExitMargin = 16;

        /// <summary>Far entities tracked (offered at least once and not forgotten since).</summary>
        public int Count => _tracks.Count;
        /// <summary>The gateways that currently hold an entity in their far tier.</summary>
        public ulong MaskOf(ulong netId) => _tracks.TryGetValue(netId, out var t) ? t.Mask : 0UL;

        /// <summary>Start a tick. Every <see cref="EvalInterval"/> the next offers are re-matched against the foci.</summary>
        public void Begin(double now)
        {
            Out.Clear();
            _now = now;
            _evaluate = now >= _nextEval;
            if (_evaluate) _nextEval = now + EvalInterval;
            if (_tracks.Count == 0) return;
            _scratch.Clear();
            foreach (var kv in _tracks) if (kv.Value.Offered) _scratch.Add(kv.Key);
            for (int i = 0; i < _scratch.Count; i++)
            {
                var t = _tracks[_scratch[i]];
                t.Offered = false;
                _tracks[_scratch[i]] = t;
            }
            _scratch.Clear();
        }

        /// <summary>Whether this tick has anything to do for the entity, so the host can skip working out its pose.</summary>
        public bool IsDue(ulong netId) => _evaluate || !_tracks.TryGetValue(netId, out var t) || _now >= t.NextAt;

        /// <summary>
        /// Hold that an entity is still far relevant this tick, without a pose: it is not due. Every tracked entity must be
        /// offered or kept each tick, or <see cref="End"/> takes it out of the tier.
        /// </summary>
        public void Keep(ulong netId)
        {
            if (!_tracks.TryGetValue(netId, out var t)) return;
            t.Offered = true;
            _tracks[netId] = t;
        }

        /// <summary>
        /// One far entity this tick, with its entry filled in (<see cref="FarEntryKind.State"/>, position absolute in its
        /// scope). <paramref name="scopeSalt"/> is <see cref="RegionKeys.SaltOf(ulong)"/> of its scope: the foci it is
        /// matched with are the ones collected in that scope's own space.
        /// </summary>
        public void Offer(in FarEntityEntry entry, ulong scopeSalt, RegionPublisher publisher, in InterestGrid grid)
        {
            bool known = _tracks.TryGetValue(entry.NetId, out var t);
            ulong mask = t.Mask;
            bool due = _now >= t.NextAt;
            if (_evaluate || !known)
            {
                ulong next = 0;
                if (publisher != null && entry.Radius > 0)
                {
                    ulong enter = publisher.WideMaskSalted(grid, scopeSalt, entry.X, entry.Y, entry.Z, entry.Radius);
                    ulong stay = mask == 0 ? 0 : publisher.WideMaskSalted(grid, scopeSalt, entry.X, entry.Y, entry.Z, entry.Radius + FarRelevance.StayMargin(entry.Radius, ExitMargin));
                    next = enter | (mask & stay);
                }
                ulong gone = mask & ~next;
                ulong fresh = next & ~mask;
                if (gone != 0) Out.Add(new Outgoing { Mask = gone, Entry = FarEntityEntry.GoneOf(entry.NetId, entry.Epoch) });
                // A gateway that has just come into reach hears now rather than at the entity's next update.
                if (fresh != 0 && !due) Out.Add(new Outgoing { Mask = fresh, Entry = entry });
                mask = next;
            }
            if (due)
            {
                if (mask != 0) Out.Add(new Outgoing { Mask = mask, Entry = entry });
                double interval = 1.0 / FarRelevance.ClampRate(entry.UpdateRate);
                t.NextAt = t.NextAt <= 0 || _now - t.NextAt > interval ? _now + interval : t.NextAt + interval;
            }
            t.Mask = mask;
            t.Epoch = entry.Epoch;
            t.Offered = true;
            _tracks[entry.NetId] = t;
        }

        /// <summary>
        /// End a tick: an entity tracked but neither offered nor kept has left the tier (its far radius was set to 0, it
        /// boarded something). The gateways that held it are sent <see cref="FarEntryKind.Gone"/>.
        /// </summary>
        public void End()
        {
            if (_tracks.Count == 0) return;
            _scratch.Clear();
            foreach (var kv in _tracks) if (!kv.Value.Offered) _scratch.Add(kv.Key);
            for (int i = 0; i < _scratch.Count; i++)
            {
                var t = _tracks[_scratch[i]];
                _tracks.Remove(_scratch[i]);
                if (t.Mask != 0) Out.Add(new Outgoing { Mask = t.Mask, Entry = FarEntityEntry.GoneOf(_scratch[i], t.Epoch) });
            }
            _scratch.Clear();
        }

        /// <summary>
        /// Stop tracking an entity without telling anyone: it was handed to another worker, which takes the tier over, or
        /// it despawned and its despawn already went to <see cref="MaskOf"/>. Returns the mask it had.
        /// </summary>
        public ulong Forget(ulong netId)
        {
            if (!_tracks.TryGetValue(netId, out var t)) return 0;
            _tracks.Remove(netId);
            return t.Mask;
        }

        /// <summary>A gateway link went away: its bit leaves every mask.</summary>
        public void RemoveGateway(int gatewayBit)
        {
            ulong bit = RegionPublisher.Bit(gatewayBit);
            if (bit == 0 || _tracks.Count == 0) return;
            _scratch.Clear();
            foreach (var kv in _tracks) if ((kv.Value.Mask & bit) != 0) _scratch.Add(kv.Key);
            for (int i = 0; i < _scratch.Count; i++)
            {
                var t = _tracks[_scratch[i]];
                t.Mask &= ~bit;
                _tracks[_scratch[i]] = t;
            }
            _scratch.Clear();
        }

        /// <summary>Match on the next tick rather than at the next interval (a gateway's foci changed).</summary>
        public void EvaluateSoon() => _nextEval = 0;

        public void Clear()
        {
            _tracks.Clear();
            Out.Clear();
        }

        /// <summary>
        /// Group <see cref="Out"/> by mask into <see cref="FarEntitiesMsg"/>es, preserving order within a mask, and hand
        /// each written message to <paramref name="send"/> with its mask. Allocation free after warm-up.
        /// </summary>
        public void Flush(NetworkWriter writer, Action<ulong> send)
        {
            if (Out.Count == 0) return;
            _groupMasks.Clear();
            for (int i = 0; i < Out.Count; i++) if (!_groupMasks.Contains(Out[i].Mask)) _groupMasks.Add(Out[i].Mask);
            for (int g = 0; g < _groupMasks.Count; g++)
            {
                ulong mask = _groupMasks[g];
                _groupEntries.Clear();
                for (int i = 0; i < Out.Count; i++) if (Out[i].Mask == mask) _groupEntries.Add(Out[i].Entry);
                for (int from = 0; from < _groupEntries.Count; from += FarEntitiesMsg.MaxEntries)
                {
                    writer.Reset();
                    FarEntitiesMsg.Write(writer, _groupEntries, from, Math.Min(FarEntitiesMsg.MaxEntries, _groupEntries.Count - from));
                    send(mask);
                }
            }
            _groupEntries.Clear();
            Out.Clear();
        }

        private readonly List<ulong> _groupMasks = new List<ulong>();
        private readonly List<FarEntityEntry> _groupEntries = new List<FarEntityEntry>();
    }
}
