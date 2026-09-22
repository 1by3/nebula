namespace Nebula
{
    /// <summary>
    /// How an interest region id is made unique to its scope. A region id is a packing of <b>absolute</b>
    /// coordinates (<see cref="InterestGrid.PackRegion"/>), and with per-scope origin frames two scopes may
    /// legitimately occupy exactly the same absolute coordinates on one worker
    /// (<c>docs/scope-frames.md</c>). Without a salt, region (3, 0, 7) of two worlds is one key in the worker's
    /// index, one entry in a gateway's subscription set and one bucket in the publisher's masks — so a worker
    /// would stream a gateway the entities of a scope that gateway has no client in, and drop them again on the
    /// per-client instance check. That was the deliberate cost recorded as <c>docs/scoped-chunk-grids.md</c> D12;
    /// this closes it.
    /// <para>
    /// The salt is an XOR with a strong mix of the scope's isolation id, so it is <b>invertible</b> by any holder
    /// that knows which scope the id belongs to — which both ends always do, because a subscription, a focus and
    /// an entity each sit in exactly one scope. That is what keeps the region arithmetic
    /// (<see cref="InterestGrid.BoundsOf"/>, <see cref="InterestGrid.SqrDistanceToRegion"/>) working on the plain
    /// packing while the keys that travel and the keys that bucket are per scope.
    /// </para>
    /// <para>
    /// <b>The public world is not salted at all</b> (<see cref="Salt"/> with 0 is the identity), so an unscoped
    /// mesh puts exactly the bytes on the wire it did before, and no message, field or protocol version changed.
    /// </para>
    /// </summary>
    public static class RegionKeys
    {
        /// <summary>SplitMix64 finalizer of the isolation id: the per-scope constant the region id is XORed with.</summary>
        public static ulong SaltOf(ulong instanceId)
        {
            if (instanceId == 0) return 0;
            ulong z = instanceId + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            // The public world owns the unsalted space; a scope must never collapse onto it.
            return z == 0 ? 1UL : z;
        }

        /// <summary>
        /// The key a region of <paramref name="instanceId"/> is known by: the plain packing in the public world,
        /// and the packing XORed with the scope's salt everywhere else.
        /// </summary>
        public static ulong Salt(ulong region, ulong instanceId) => region ^ SaltOf(instanceId);

        /// <summary>The inverse of <see cref="Salt"/>: the plain packing the region arithmetic works on.</summary>
        public static ulong Unsalt(ulong key, ulong instanceId) => key ^ SaltOf(instanceId);
    }
}
