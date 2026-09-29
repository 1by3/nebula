using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The arithmetic of relevance tiers (<see cref="RelevanceTiers"/>, <c>docs/server-owned-entities.md</c> D3): the
    /// priority's bits in a spawn's interest flags, the distance bands, each priority's divisor, and the rate window a
    /// gateway sends a client one entry per. Pure C#, so it runs in the package and in the service tests.
    /// </summary>
    public sealed class RelevanceTiersTests
    {
        [Test]
        public void ThePriorityRidesInTheInterestFlagsBesideAlwaysRelevant()
        {
            foreach (RelevancePriority p in System.Enum.GetValues(typeof(RelevancePriority)))
            {
                var flags = RelevanceTiers.WithPriority(EntityInterestFlags.AlwaysRelevant, p);
                Assert.AreEqual(p, RelevanceTiers.PriorityOf(flags), $"{p} round-trips");
                Assert.IsTrue((flags & EntityInterestFlags.AlwaysRelevant) != 0, $"{p} leaves AlwaysRelevant alone");
                var bytes = new NetworkWriter();
                new EntitySpawnMsg { InterestFlags = flags, OwnerIdentity = "" }.Write(bytes, MsgId.EntitySpawn);
                var reader = new NetworkReader(bytes.ToSegment());
                reader.ReadByte();
                Assert.AreEqual(p, RelevanceTiers.PriorityOf(EntitySpawnMsg.Read(reader).InterestFlags), $"{p} survives the spawn's wire format");
            }
            Assert.AreEqual(RelevancePriority.Normal, RelevanceTiers.PriorityOf(EntityInterestFlags.AlwaysRelevant), "a sender that sets no priority sends Normal");
        }

        [Test]
        public void TheBandIsNearWithinTheNearRadiusMiddleWithinTheFarRadiusAndFarBeyond()
        {
            Assert.AreEqual(RelevanceTiers.Near, RelevanceTiers.BandOf(30 * 30, 30f, 80f));
            Assert.AreEqual(RelevanceTiers.Middle, RelevanceTiers.BandOf(30.1 * 30.1, 30f, 80f));
            Assert.AreEqual(RelevanceTiers.Middle, RelevanceTiers.BandOf(80 * 80, 30f, 80f));
            Assert.AreEqual(RelevanceTiers.Far, RelevanceTiers.BandOf(80.1 * 80.1, 30f, 80f));
            Assert.AreEqual(RelevanceTiers.Far, RelevanceTiers.BandOf(double.MaxValue, 30f, 80f), "no focus in the entity's space is far");
        }

        [Test]
        public void EachPriorityPicksItsDivisorPerBand()
        {
            const int mid = 4, far = 12;
            int[] Row(RelevancePriority p) => new[]
            {
                RelevanceTiers.Divisor(p, RelevanceTiers.Near, mid, far),
                RelevanceTiers.Divisor(p, RelevanceTiers.Middle, mid, far),
                RelevanceTiers.Divisor(p, RelevanceTiers.Far, mid, far),
            };
            CollectionAssert.AreEqual(new[] { 1, 1, mid }, Row(RelevancePriority.High));
            CollectionAssert.AreEqual(new[] { 1, mid, far }, Row(RelevancePriority.Normal), "Normal is the configured tiers, as before");
            CollectionAssert.AreEqual(new[] { mid, far, far }, Row(RelevancePriority.Low));
            CollectionAssert.AreEqual(new[] { mid, RelevanceTiers.Quiet, RelevanceTiers.Quiet }, Row(RelevancePriority.Background));
            Assert.AreEqual(1, RelevanceTiers.Divisor(RelevancePriority.Normal, RelevanceTiers.Middle, 0, 0), "a divisor below 1 is every update");
        }

        /// <summary>Sends per 600 ticks for an entity the worker sends every <paramref name="interval"/> ticks, to a client whose window is <paramref name="divisor"/>.</summary>
        private static int Sent(ulong netId, int interval, int divisor, uint start = 1000)
        {
            // As if the entity had been sent on the tick before the count starts, so the count is steady state.
            int sent = 0;
            bool has = true;
            uint previous = start - 1;
            for (uint tick = start; tick < start + 600; tick++)
            {
                if ((tick + netId % (ulong)interval) % (ulong)interval != 0) continue; // the worker's stagger
                if (RelevanceTiers.StartsWindow(tick, has, previous, netId, divisor)) sent++;
                has = true;
                previous = tick;
            }
            return sent;
        }

        [Test]
        public void AnEveryTickEntityGetsOneEntryPerWindowAsTheOldTickFilterGaveIt()
        {
            for (ulong id = 1; id < 40; id++)
            {
                Assert.AreEqual(600, Sent(id, 1, 1));
                Assert.AreEqual(150, Sent(id, 1, 4), $"#{id}: 15 Hz");
                Assert.AreEqual(50, Sent(id, 1, 12), $"#{id}: 5 Hz");
            }
        }

        [Test]
        public void TheWindowComposesWithTheWorkersUpdateIntervalForEveryNetId()
        {
            // Every net id, not just the lucky ones: a tick filter ((tick + id % d) % d == 0) never matches an entity
            // sent every 6 ticks for some ids when d = 4, and would have left it frozen on the client.
            for (ulong id = 1; id < 40; id++)
            {
                Assert.That(Sent(id, 6, 4), Is.InRange(99, 100), $"#{id}: every update of a 10 Hz entity to a 15 Hz window");
                Assert.That(Sent(id, 6, 12), Is.InRange(49, 51), $"#{id}: every other update to a 5 Hz window");
                Assert.That(Sent(id, 30, 12), Is.InRange(19, 20), $"#{id}: a 2 Hz entity is not slowed further by a 5 Hz window");
                Assert.That(Sent(id, 6, 60), Is.InRange(9, 11), $"#{id}: one a second");
            }
        }

        [Test]
        public void AQuietWindowSendsNothingAndAFirstEntryAlwaysGoes()
        {
            Assert.AreEqual(0, Sent(7, 1, RelevanceTiers.Quiet));
            Assert.IsTrue(RelevanceTiers.StartsWindow(500, false, 0, 7, 60), "the first entry after a spawn goes");
            Assert.IsTrue(RelevanceTiers.StartsWindow(10, true, 20, 7, 60), "an entry behind the previous one (a new epoch) goes");
            Assert.IsFalse(RelevanceTiers.StartsWindow(500, true, 400, 7, RelevanceTiers.Quiet));
        }

        [Test]
        public void WindowsAreStaggeredByNetIdSoACrowdSpreadsOverTheTicks()
        {
            // 60 entities updated every tick, each to a 12-tick window: every tick carries about 5 of them.
            var perTick = new int[12];
            for (ulong id = 0; id < 60; id++)
                for (uint tick = 1200; tick < 1212; tick++)
                    if (RelevanceTiers.StartsWindow(tick, true, tick - 1, id, 12)) perTick[tick - 1200]++;
            foreach (int n in perTick) Assert.AreEqual(5, n);
        }
    }
}
