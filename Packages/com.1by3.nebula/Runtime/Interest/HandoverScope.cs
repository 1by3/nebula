using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// The bookkeeping one whole-subtree authority handoff needs: how deep the recursive transfer currently is,
    /// and which entities are leaving with the carrier being handed over right now.
    /// <para>
    /// A handoff moves a ship and everything riding in it as one unit — the carrier first, then its contents, on
    /// the same ordered stream — so taking the carrier out of the interest index in between is not an orphaning
    /// and must publish nothing for the passengers that are coming along (design D85). The set is populated by
    /// the outermost transfer and read by <see cref="CarriedTransition.Resolve{T}"/>, which collapses a
    /// follower's transition to "nothing changed" so no gateway is sent a passenger that is about to be
    /// redirected anyway.
    /// </para>
    /// <para>
    /// Scopes are entered with <see cref="Begin"/> in a <c>using</c>, so a throwing persistence store, handover
    /// callback or nested transfer cannot leave a nonzero depth and a stale follower set behind: the next
    /// top-level handoff would then skip its own collection and republish its passengers as orphans that never
    /// existed (design D87). Empty and free outside a handover, which is every steady-state call.
    /// </para>
    /// <para>One instance per worker, reused; the only state is the set and a scratch list.</para>
    /// </summary>
    public sealed class HandoverScope
    {
        private readonly HashSet<ulong> _followers = new HashSet<ulong>();
        private readonly List<ulong> _scratch = new List<ulong>();
        private int _depth;

        /// <summary>How many transfers are in flight; 0 outside a handover.</summary>
        public int Depth => _depth;

        /// <summary>How many entities are recorded as leaving with the carrier being handed over.</summary>
        public int FollowerCount => _followers.Count;

        /// <summary>
        /// Whether this entity is moving to another worker with the carrier it rides in, so the transition the
        /// index just went through is internal to a handoff and nothing about it may be published.
        /// </summary>
        public bool Follows(ulong netId) => _followers.Count > 0 && _followers.Contains(netId);

        /// <summary>Record one entity as leaving with the carrier. False when it was already recorded.</summary>
        public bool Add(ulong netId) => _followers.Add(netId);

        /// <summary>
        /// Record everything riding in <paramref name="carrier"/> in the index, to any depth, as leaving with it.
        /// This is the index-driven collection, for a host whose whole idea of "what is inside what" is the
        /// carrier links; a host that also knows about interiors pinned to a worker of their own walks its own
        /// tree instead and calls <see cref="Add"/>, because a pinned interior stays behind and is really
        /// orphaned. Returns how many ids were newly recorded.
        /// </summary>
        public int Collect<T>(InterestIndex<T> index, ulong carrier)
        {
            if (index == null || !index.HasCarried(carrier)) return 0;
            _scratch.Clear();
            index.CollectCarried(carrier, _scratch);
            int added = 0;
            for (int i = 0; i < _scratch.Count; i++) if (_followers.Add(_scratch[i])) added++;
            _scratch.Clear();
            return added;
        }

        /// <summary>
        /// Enter a transfer. The returned frame must be disposed — in a <c>using</c> or a <c>finally</c> — on
        /// every path out of the transfer, including an exceptional one; disposing the outermost frame clears
        /// the follower set.
        /// </summary>
        public Frame Begin() => new Frame(this, ++_depth == 1);

        /// <summary>Forget everything (a worker being torn down, or a host restarting its bookkeeping).</summary>
        public void Clear()
        {
            _depth = 0;
            _followers.Clear();
            _scratch.Clear();
        }

        private void End()
        {
            if (--_depth > 0) return;
            _depth = 0;
            _followers.Clear();
        }

        /// <summary>One transfer's hold on the scope. A struct, so a handoff of a deep ship allocates nothing.</summary>
        public readonly struct Frame : IDisposable
        {
            private readonly HandoverScope _scope;

            /// <summary>True for the transfer that opened the handoff: the one that owns the follower set.</summary>
            public bool IsOutermost { get; }

            internal Frame(HandoverScope scope, bool outermost)
            {
                _scope = scope;
                IsOutermost = outermost;
            }

            public void Dispose() => _scope?.End();
        }
    }
}
