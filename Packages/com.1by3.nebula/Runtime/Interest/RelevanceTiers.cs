namespace Nebula
{
    /// <summary>
    /// How much an entity's transform updates matter to the clients that hold it, relative to its distance from them
    /// (<see cref="NetworkIdentity.RelevancePriority"/>, <c>docs/server-owned-entities.md</c> D3). The gateway picks
    /// each client's update rate for an entity from its distance band (<see cref="InterestSettings.NearRadius"/>,
    /// <see cref="InterestSettings.FarRadius"/>) and this priority:
    /// <list type="table">
    /// <listheader><term>Priority</term><description>near / middle / far</description></listheader>
    /// <item><term><see cref="High"/></term><description>every update / every update / <c>InterestMidDivisor</c></description></item>
    /// <item><term><see cref="Normal"/></term><description>every update / <c>InterestMidDivisor</c> / <c>InterestFarDivisor</c></description></item>
    /// <item><term><see cref="Low"/></term><description><c>InterestMidDivisor</c> / <c>InterestFarDivisor</c> / <c>InterestFarDivisor</c></description></item>
    /// <item><term><see cref="Background"/></term><description><c>InterestMidDivisor</c> / none / none</description></item>
    /// </list>
    /// "Every update" is every update the worker sends, which <see cref="NetworkIdentity.UpdateInterval"/> may already
    /// have slowed. "None" stops a client's updates without despawning the entity there: the client keeps it where it
    /// was last told, and still receives its reliable entries (where it came to rest, a teleport, a container change).
    /// A client's own pawn is always sent every update.
    /// <para>
    /// That table is the shift a priority gets by default. <see cref="High"/>, <see cref="Low"/> and
    /// <see cref="Background"/> can instead have tiers of their own (<see cref="NebulaConfig.InterestHighTiers"/>,
    /// <see cref="NebulaConfig.InterestLowTiers"/>, <see cref="NebulaConfig.InterestBackgroundTiers"/>), so a crowd can be
    /// slowed while players, at <see cref="Normal"/>, keep the global tiers.
    /// </para>
    /// </summary>
    public enum RelevancePriority : byte
    {
        /// <summary>The configured distance tiers, unchanged. The default.</summary>
        Normal = 0,
        /// <summary>One tier better than its distance: every update out to <c>InterestFarRadius</c>. A boss, an escort target.</summary>
        High = 1,
        /// <summary>One tier worse than its distance. Traffic, a crowd member, a critter.</summary>
        Low = 2,
        /// <summary>
        /// Updated only inside <c>InterestNearRadius</c>, at the middle band's rate; beyond it a client is told only
        /// where the entity comes to rest. For the many entities that matter only up close: townsfolk, ambient life.
        /// </summary>
        Background = 3,
    }

    /// <summary>
    /// A priority's own distance tiers (<see cref="NebulaConfig.InterestLowTiers"/> and its siblings,
    /// <c>docs/server-owned-entities.md</c> D10): two radii and a divisor for each of the three bands they make. When
    /// <see cref="Override"/> is false the priority keeps the shift of the global tiers that
    /// <see cref="RelevancePriority"/> describes, and the other fields are ignored, so a config that sets none of
    /// this behaves exactly as before.
    /// <para>
    /// A divisor is ticks per update window, as everywhere in the tiers: 1 is every update the worker sends, 4 one
    /// update in each window of 4 ticks, and 0 none at all (the client keeps the entity where it was last told, and
    /// is still sent its reliable entries).
    /// </para>
    /// </summary>
    [System.Serializable]
    public struct RelevanceTierBands
    {
        /// <summary>Use these bands for the priority instead of shifting the global ones.</summary>
        public bool Override;
        /// <summary>Metres from a client's nearest focus within which an entity is in the near band.</summary>
        public float NearRadius;
        /// <summary>Metres within which it is in the middle band; beyond it, the far band.</summary>
        public float FarRadius;
        /// <summary>Ticks per update window in the near band (1 = every update, 0 = none).</summary>
        public int NearDivisor;
        /// <summary>Ticks per update window in the middle band (0 = none).</summary>
        public int MidDivisor;
        /// <summary>Ticks per update window in the far band, and for a client with no focus in the entity's space (0 = none).</summary>
        public int FarDivisor;

        /// <summary>Bands set explicitly (<see cref="Override"/> true).</summary>
        public static RelevanceTierBands Of(float nearRadius, float farRadius, int nearDivisor, int midDivisor, int farDivisor) => new RelevanceTierBands
        {
            Override = true, NearRadius = nearRadius, FarRadius = farRadius,
            NearDivisor = nearDivisor, MidDivisor = midDivisor, FarDivisor = farDivisor,
        };

        /// <summary>The divisor of distance band <paramref name="band"/> (<see cref="RelevanceTiers.Near"/>, <see cref="RelevanceTiers.Middle"/>, <see cref="RelevanceTiers.Far"/>).</summary>
        public int DivisorOf(int band) => band == RelevanceTiers.Near ? NearDivisor : band == RelevanceTiers.Middle ? MidDivisor : FarDivisor;

        public override string ToString() => Override
            ? $"near {NearRadius} m /{NearDivisor}, to {FarRadius} m /{MidDivisor}, beyond /{FarDivisor}"
            : "the global tiers, shifted";
    }

    /// <summary>The arithmetic of <see cref="RelevancePriority"/>: pure, shared by the gateway, the worker and the tests.</summary>
    public static class RelevanceTiers
    {
        /// <summary>The bits of <see cref="EntityInterestFlags"/> that carry the priority.</summary>
        public const EntityInterestFlags PriorityMask = (EntityInterestFlags)0b0000_0110;
        private const int PriorityShift = 1;

        /// <summary>Distance bands, nearest first.</summary>
        public const int Near = 0, Middle = 1, Far = 2;

        /// <summary>A divisor meaning "send this client no unreliable updates for this entity".</summary>
        public const int Quiet = 0;

        /// <summary><paramref name="flags"/> with its priority bits set to <paramref name="priority"/>.</summary>
        public static EntityInterestFlags WithPriority(EntityInterestFlags flags, RelevancePriority priority) =>
            (flags & ~PriorityMask) | (EntityInterestFlags)(((int)priority << PriorityShift) & (int)PriorityMask);

        /// <summary>The priority a spawn's interest flags carry; <see cref="RelevancePriority.Normal"/> from a sender that sets none.</summary>
        public static RelevancePriority PriorityOf(EntityInterestFlags flags) =>
            (RelevancePriority)(((int)flags & (int)PriorityMask) >> PriorityShift);

        /// <summary>
        /// The distance band of a squared distance: <see cref="Near"/> within the near radius, <see cref="Middle"/>
        /// within the far radius, <see cref="Far"/> beyond it or when there is no distance at all
        /// (<see cref="double.MaxValue"/>: a client with no focus in the entity's space).
        /// </summary>
        public static int BandOf(double sqrDistance, float nearRadius, float farRadius)
        {
            if (sqrDistance == double.MaxValue) return Far;
            if (sqrDistance <= (double)nearRadius * nearRadius) return Near;
            return sqrDistance <= (double)farRadius * farRadius ? Middle : Far;
        }

        /// <summary>
        /// Ticks per update window for an entity of <paramref name="priority"/> in distance band
        /// <paramref name="band"/>: 1 is every update, <see cref="Quiet"/> is none (see <see cref="RelevancePriority"/>).
        /// </summary>
        public static int Divisor(RelevancePriority priority, int band, int midDivisor, int farDivisor)
        {
            int mid = midDivisor < 1 ? 1 : midDivisor;
            int far = farDivisor < 1 ? 1 : farDivisor;
            switch (priority)
            {
                case RelevancePriority.High: return band == Far ? mid : 1;
                case RelevancePriority.Low: return band == Near ? mid : far;
                case RelevancePriority.Background: return band == Near ? mid : Quiet;
                default: return band == Near ? 1 : band == Middle ? mid : far;
            }
        }

        /// <summary>
        /// The bands a priority uses under the global tiers <paramref name="nearRadius"/>, <paramref name="farRadius"/>,
        /// <paramref name="midDivisor"/> and <paramref name="farDivisor"/>: the priority's own when it has an
        /// <see cref="RelevanceTierBands.Override"/>, otherwise the global ones shifted as
        /// <see cref="Divisor(RelevancePriority, int, int, int)"/> says. <see cref="RelevancePriority.Normal"/> always
        /// uses the global tiers: they are its tiers.
        /// </summary>
        public static RelevanceTierBands Resolve(RelevancePriority priority, in RelevanceTierBands own, float nearRadius, float farRadius, int midDivisor, int farDivisor)
        {
            if (own.Override && priority != RelevancePriority.Normal) return own;
            return new RelevanceTierBands
            {
                Override = false,
                NearRadius = nearRadius,
                FarRadius = farRadius,
                NearDivisor = Divisor(priority, Near, midDivisor, farDivisor),
                MidDivisor = Divisor(priority, Middle, midDivisor, farDivisor),
                FarDivisor = Divisor(priority, Far, midDivisor, farDivisor),
            };
        }

        /// <summary>
        /// Ticks per update window for a client at squared distance <paramref name="sqrDistance"/> from an entity whose
        /// priority resolved to <paramref name="bands"/> (<see cref="Resolve"/>); <see cref="Quiet"/> is none.
        /// </summary>
        public static int DivisorAt(in RelevanceTierBands bands, double sqrDistance)
        {
            int d = bands.DivisorOf(BandOf(sqrDistance, bands.NearRadius, bands.FarRadius));
            return d < 0 ? 1 : d;
        }

        /// <summary>
        /// Whether an entry at <paramref name="tick"/> starts a new window of <paramref name="divisor"/> ticks for
        /// this entity, given the tick of the entity's previous entry. A client whose rate is one update per
        /// window is sent the first entry of each window and nothing else in it. Windows are staggered by net id so
        /// a crowd's updates spread over the ticks of a window rather than landing on one.
        /// <para>
        /// This, and not "is the tick a multiple of the divisor", is what lets a gateway's rate compose with a
        /// worker that sends an entity only every <see cref="NetworkIdentity.UpdateInterval"/> ticks: an entity
        /// updated every 6 ticks and a client asking for every 4th tick get every update, where the tick test would
        /// match some entities never.
        /// </para>
        /// </summary>
        public static bool StartsWindow(uint tick, bool hasPrevious, uint previousTick, ulong netId, int divisor)
        {
            if (divisor == Quiet) return false;
            if (divisor <= 1 || !hasPrevious || tick <= previousTick) return true;
            ulong d = (ulong)divisor;
            ulong offset = netId % d;
            return (tick + offset) / d != (previousTick + offset) / d;
        }
    }
}
