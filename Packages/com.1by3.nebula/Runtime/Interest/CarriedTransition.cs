using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// The bookkeeping a rebucket of a <b>carrier</b> needs: where the carrier and everything riding
    /// in it sat before the index moved them, and which gateways each of them gains or loses as a result.
    /// <para>
    /// <see cref="InterestIndex{T}"/> moves a carrier's whole subtree as one unit, but the subscriber-mask
    /// transition has to be published per entity: a gateway subscribing only the destination region has never
    /// heard of the passengers, and one subscribing only the origin region keeps them for ever unless it is told
    /// to forget them. Capture before the move, resolve after it, then walk the slots forwards to spawn (carriers
    /// before their contents) and backwards to forget (contents before their carriers).
    /// </para>
    /// <para>
    /// "Where it sat" includes <i>how</i> it was published, not only which bucket it was in: boarding turns an
    /// always-relevant crate into part of a ship and getting off turns it back, and both are transitions that
    /// have to be sent.
    /// </para>
    /// <para>
    /// One instance per publisher, reused: the scratch lists are the only state, so a carrier with nothing riding
    /// in it costs one list entry and no allocation.
    /// </para>
    /// </summary>
    public sealed class CarriedTransition
    {
        /// <summary>One entity of the moved subtree: where it was, and the gateways that hear it before and after.</summary>
        public struct Slot
        {
            public ulong Id;
            /// <summary>Placement and region bucket before the change.</summary>
            public InterestPlacement From;
            public ulong FromRegion;
            /// <summary>Placement after the change; equal to <see cref="From"/> for an entity that only changed bucket.</summary>
            public InterestPlacement To;
            /// <summary>Region bucket after the change (meaningful when <see cref="To"/> is <see cref="InterestPlacement.Region"/>).</summary>
            public ulong ToRegion;
            /// <summary>
            /// Gateways that hear about the entity before and after, with no sticky set folded in: the owner's
            /// gateway and explicit subscribers differ per host, so the caller adds them with
            /// <c>RebucketBits</c>. Equal masks mean nothing has to be published for this entity.
            /// <para>
            /// This covers reclassification as well as a bucket change: a passenger that was global
            /// and is now bucketed with its ship has every linked gateway in <see cref="Before"/> and only the
            /// ship's subscribers in <see cref="After"/>, so the gateways that were keeping it because of its own
            /// prefab settings are told to forget it.
            /// </para>
            /// </summary>
            public ulong Before, After;
        }

        private readonly List<ulong> _ids = new List<ulong>();
        private readonly List<Slot> _slots = new List<Slot>();

        /// <summary>Entities in the captured subtree, the carrier first.</summary>
        public int Count => _slots.Count;
        public Slot this[int i] => _slots[i];

        /// <summary>
        /// Record <paramref name="root"/> and everything it carries, carriers before their contents, <b>before</b>
        /// the index is changed, together with the gateways each of them reaches right now.
        /// <para>
        /// A root that is not in the index yet still captures its subtree: a passenger can be indexed before the
        /// carrier its container names, and adding that carrier re-seats the whole pending subtree into the
        /// carrier's placement. That is an implicit change with the same publication consequences as a rebucket,
        /// so it is captured the same way — with no slot for the root, which has nothing to say yet.
        /// </para>
        /// <para>
        /// <paramref name="wideMaskOf"/> answers "which gateways hold this wide entity", which only the caller
        /// knows: a wide entity is matched against foci at the evaluation rate and the answer is remembered, not
        /// derived from a bucket. It is not asked about anything that is not wide.
        /// </para>
        /// </summary>
        public void Capture<T>(InterestIndex<T> index, ulong root, RegionPublisher publisher, Func<ulong, ulong> wideMaskOf = null)
        {
            _ids.Clear();
            _slots.Clear();
            if (index == null) return;
            if (index.TryGetPlacement(root, out var placement, out ulong region))
                _slots.Add(new Slot { Id = root, From = placement, FromRegion = region, Before = MaskOf(publisher, wideMaskOf, root, placement, region) });
            if (!index.HasCarried(root)) return;
            index.CollectCarried(root, _ids);
            for (int i = 0; i < _ids.Count; i++)
            {
                ulong id = _ids[i];
                if (!index.TryGetPlacement(id, out var childPlacement, out ulong childRegion)) continue;
                _slots.Add(new Slot
                {
                    Id = id, From = childPlacement, FromRegion = childRegion,
                    Before = MaskOf(publisher, wideMaskOf, id, childPlacement, childRegion),
                });
            }
        }

        /// <summary>
        /// Fill in each slot's "after" masks, <b>after</b> the index has been changed. An entity that left the
        /// index in between gets equal masks: it is gone, and a despawn says that better than a forget. That is
        /// also what makes this the right capture for a carrier being <i>removed</i> — the carrier's own slot
        /// publishes nothing while the passengers it orphaned publish the placement each of them got back.
        /// <para>
        /// Both ends are read the same way, so a passenger that changed <i>class</i> — global or wide before,
        /// bucketed with its carrier after, or the other way round when it gets off — publishes exactly the
        /// gateways it gained and lost, rather than being written off as "not a region entity".
        /// </para>
        /// <para>
        /// <paramref name="handover"/>, when a handoff is in flight, names the passengers that are leaving with
        /// the carrier. Their transition is collapsed to "nothing changed": the subtree is moving as one unit and
        /// publishing the intermediate state would send an always-relevant crate to gateways that then get
        /// neither a redirect nor a forget and cache it for ever (design D85). Contents that really stay behind —
        /// a pinned interior — are not in that set and are published exactly as a despawned carrier's survivors
        /// are. Null outside a handover, which is every steady-state call.
        /// </para>
        /// </summary>
        public void Resolve<T>(InterestIndex<T> index, RegionPublisher publisher, Func<ulong, ulong> wideMaskOf = null, HandoverScope handover = null)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                if (!index.TryGetPlacement(slot.Id, out var to, out ulong toRegion))
                {
                    slot.To = slot.From;
                    slot.ToRegion = slot.FromRegion;
                    slot.After = slot.Before;
                    _slots[i] = slot;
                    continue;
                }
                slot.To = to;
                slot.ToRegion = to == InterestPlacement.Region ? toRegion : slot.FromRegion;
                // The placement is still resolved for a follower: the caller reads it to drop stale wide masks,
                // and the entity really is where the index now says. Only the publication is suppressed.
                slot.After = handover != null && handover.Follows(slot.Id)
                    ? slot.Before
                    : MaskOf(publisher, wideMaskOf, slot.Id, to, toRegion);
                _slots[i] = slot;
            }
        }

        /// <summary>The gateways one entity reaches, by the rule its placement implies. Pure.</summary>
        private static ulong MaskOf(RegionPublisher publisher, Func<ulong, ulong> wideMaskOf, ulong id, InterestPlacement where, ulong region)
        {
            if (publisher == null) return 0;
            return where switch
            {
                // Always relevant: every gateway linked to this publisher holds it, and loses it the moment it
                // stops being global - which is what boarding an ordinary ship does to it.
                InterestPlacement.Global => publisher.LinkedMask,
                InterestPlacement.Wide => wideMaskOf != null ? wideMaskOf(id) : 0,
                _ => publisher.MaskOf(region),
            };
        }

        /// <summary>Forget the captured subtree (a host that abandons a move rather than publishing it).</summary>
        public void Clear()
        {
            _ids.Clear();
            _slots.Clear();
        }
    }
}
