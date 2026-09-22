using System.Collections.Generic;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// Per-client interest (design §4): what enters a client's set, what stays in it, and above all what it
    /// takes to leave. The hysteresis and the linger are the difference between a player standing on a boundary
    /// seeing a stable world and seeing entities blink several times a second.
    /// </summary>
    public class ClientInterestTests
    {
        private struct Record
        {
            public double X, Y, Z;
            public float Radius;
            public bool Always;
            public ulong Carrier;
            /// <summary>Stands in for anything the game refuses: another team, another instance, an unknown container.</summary>
            public bool Secret;
        }

        private struct Source : IInterestSource<Record>
        {
            public void Describe(ulong id, in Record value, out InterestEntity entity) => entity = new InterestEntity
            {
                NetId = id, X = value.X, Y = value.Y, Z = value.Z,
                RelevanceRadius = value.Radius, AlwaysRelevant = value.Always,
                CarrierNetId = value.Carrier, InterestGroup = value.Secret ? (byte)1 : (byte)0,
            };

            public bool Authorize(in InterestClient client, in InterestEntity entity) => entity.InterestGroup == 0;
        }

        private static InterestSettings Settings()
        {
            var s = InterestSettings.Default;
            s.Radius = 100;
            s.ExitMargin = 20;
            s.LingerSeconds = 2;
            s.CellSize = 64;
            s.Planar = true;
            return s;
        }

        private ClientInterest<Record, Source> Interest(out InterestIndex<Record> index)
        {
            var settings = Settings();
            index = new InterestIndex<Record>();
            var interest = new ClientInterest<Record, Source>(new Source())
            {
                Settings = settings,
                Grid = InterestGrid.Resolve(settings),
                Client = new InterestClient { ClientId = 1, HasPawn = true, PawnNetId = 1 },
            };
            interest.SetFoci(new List<InterestFocus> { InterestFocus.Point(0, 0, 0) });
            return interest;
        }

        private static void Place(InterestIndex<Record> index, InterestGrid grid, ulong id, double x, double z, float radius = 0, bool always = false, bool secret = false)
        {
            var record = new Record { X = x, Z = z, Radius = radius, Always = always, Secret = secret };
            if (always) index.AddGlobal(id, record);
            else if (radius > InterestSettings.Default.Radius) index.AddWide(id, record);
            else index.Add(id, grid.RegionOf(x, 0, z), record);
        }

        private readonly List<ulong> _entered = new List<ulong>();
        private readonly List<ulong> _left = new List<ulong>();

        private void Evaluate(ClientInterest<Record, Source> interest, InterestIndex<Record> index, double now)
        {
            _entered.Clear();
            _left.Clear();
            interest.Evaluate(index, now, _entered, _left);
        }

        [Test]
        public void AnEntityInsideTheRadiusEnters()
        {
            var interest = Interest(out var index);
            Place(index, interest.Grid, 10, 50, 0);
            Place(index, interest.Grid, 11, 400, 0);
            Evaluate(interest, index, 0);
            CollectionAssert.AreEqual(new ulong[] { 10 }, _entered);
            Assert.IsTrue(interest.Contains(10));
            Assert.IsFalse(interest.Contains(11));
            Evaluate(interest, index, 1);
            CollectionAssert.IsEmpty(_entered, "a stable set produces no events");
        }

        [Test]
        public void LeavingTakesTheExitMarginAndThenTheLinger()
        {
            var interest = Interest(out var index);
            Place(index, interest.Grid, 10, 50, 0);
            Evaluate(interest, index, 0);
            Assert.IsTrue(interest.Contains(10));

            // Past the radius but inside the margin: it stays, and no clock is started.
            index.Add(10, interest.Grid.RegionOf(110, 0, 0), new Record { X = 110 });
            Evaluate(interest, index, 1);
            CollectionAssert.IsEmpty(_left);

            // Past the margin: the linger starts now, not when it crossed the radius.
            index.Add(10, interest.Grid.RegionOf(130, 0, 0), new Record { X = 130 });
            Evaluate(interest, index, 2);
            CollectionAssert.IsEmpty(_left);
            Evaluate(interest, index, 3.9);
            CollectionAssert.IsEmpty(_left);
            Evaluate(interest, index, 4.1);
            CollectionAssert.AreEqual(new ulong[] { 10 }, _left);
            Assert.IsFalse(interest.Contains(10));
        }

        [Test]
        public void ComingBackInsideCancelsTheLinger()
        {
            var interest = Interest(out var index);
            Place(index, interest.Grid, 10, 50, 0);
            Evaluate(interest, index, 0);
            index.Add(10, interest.Grid.RegionOf(200, 0, 0), new Record { X = 200 });
            Evaluate(interest, index, 1);
            index.Add(10, interest.Grid.RegionOf(50, 0, 0), new Record { X = 50 });
            Evaluate(interest, index, 2);
            index.Add(10, interest.Grid.RegionOf(200, 0, 0), new Record { X = 200 });
            Evaluate(interest, index, 2.5);
            Evaluate(interest, index, 4.4);
            CollectionAssert.IsEmpty(_left, "the linger restarted when it came back");
            Evaluate(interest, index, 4.6);
            CollectionAssert.AreEqual(new ulong[] { 10 }, _left);
        }

        [Test]
        public void AuthorizationFailureRemovesAtOnce()
        {
            var interest = Interest(out var index);
            Place(index, interest.Grid, 10, 50, 0);
            Evaluate(interest, index, 0);
            Assert.IsTrue(interest.Contains(10));
            index.Add(10, interest.Grid.RegionOf(50, 0, 0), new Record { X = 50, Secret = true });
            Evaluate(interest, index, 0.1);
            CollectionAssert.AreEqual(new ulong[] { 10 }, _left, "a security boundary does not linger");
            Assert.IsFalse(interest.Contains(10));
        }

        [Test]
        public void AnUnauthorizedEntityNeverEnters()
        {
            var interest = Interest(out var index);
            Place(index, interest.Grid, 10, 10, 0, secret: true);
            Evaluate(interest, index, 0);
            CollectionAssert.IsEmpty(_entered);
        }

        [Test]
        public void AnEntityIsMeasuredToItsNearestFocus()
        {
            var interest = Interest(out var index);
            interest.SetFoci(new List<InterestFocus> { InterestFocus.Point(0, 0, 0), InterestFocus.Point(1000, 0, 0) });
            Place(index, interest.Grid, 10, 950, 0);
            Place(index, interest.Grid, 11, 500, 0);
            Evaluate(interest, index, 0);
            CollectionAssert.Contains(_entered, 10UL);
            CollectionAssert.DoesNotContain(_entered, 11UL, "halfway between two foci is near neither");
        }

        [Test]
        public void AFocusCanScaleItsOwnRadius()
        {
            var interest = Interest(out var index);
            interest.SetFoci(new List<InterestFocus> { InterestFocus.Point(0, 0, 0, radiusScale: 0.5f) });
            Place(index, interest.Grid, 10, 40, 0);
            Place(index, interest.Grid, 11, 80, 0);
            Evaluate(interest, index, 0);
            CollectionAssert.AreEqual(new ulong[] { 10 }, _entered);
        }

        [Test]
        public void GlobalEntitiesAreInEverySetAtAnyDistance()
        {
            var interest = Interest(out var index);
            Place(index, interest.Grid, 10, 100000, 0, always: true);
            Evaluate(interest, index, 0);
            CollectionAssert.AreEqual(new ulong[] { 10 }, _entered);
            Evaluate(interest, index, 100);
            CollectionAssert.IsEmpty(_left);
        }

        [Test]
        public void AGlobalEntityIsStillAuthorized()
        {
            var interest = Interest(out var index);
            index.AddGlobal(10, new Record { X = 5, Always = true, Secret = true });
            Evaluate(interest, index, 0);
            CollectionAssert.IsEmpty(_entered, "an always-relevant prefab is not a way around the security filter");
        }

        [Test]
        public void TheAlwaysSetKeepsAnEntityTheDistanceWouldDrop()
        {
            var interest = Interest(out var index);
            Place(index, interest.Grid, 10, 5000, 0);
            interest.SetAlways(new List<ulong> { 10 });
            Evaluate(interest, index, 0);
            CollectionAssert.AreEqual(new ulong[] { 10 }, _entered);
            interest.SetAlways(new List<ulong>());
            Evaluate(interest, index, 0.1);
            Evaluate(interest, index, 3);
            CollectionAssert.AreEqual(new ulong[] { 10 }, _left, "once it is no longer an extra, the distance decides again");
        }

        [Test]
        public void AWideEntityIsFoundByItsOwnRadius()
        {
            var interest = Interest(out var index);
            // 400 m away with a 500 m relevance radius: no client-side scan would ever reach it.
            index.AddWide(10, new Record { X = 400, Radius = 500 });
            index.AddWide(11, new Record { X = 400, Radius = 150 });
            Evaluate(interest, index, 0);
            CollectionAssert.AreEqual(new ulong[] { 10 }, _entered);
        }

        [Test]
        public void APrefabRadiusIsClampedToTheMeshCeiling()
        {
            var interest = Interest(out var index);
            var settings = interest.Settings;
            settings.MaxRadius = 200;
            interest.Settings = settings;
            index.AddWide(10, new Record { X = 300, Radius = 5000 });
            Evaluate(interest, index, 0);
            CollectionAssert.IsEmpty(_entered, "a prefab cannot ask to be visible from further than the mesh allows");
        }

        [Test]
        public void CarriersArriveBeforeTheirContentsAndLeaveAfterThem()
        {
            var interest = Interest(out var index);
            index.Add(1, interest.Grid.RegionOf(50, 0, 0), new Record { X = 50 });
            index.Add(2, interest.Grid.RegionOf(50, 0, 0), new Record { X = 50, Carrier = 1 });
            index.Add(3, interest.Grid.RegionOf(50, 0, 0), new Record { X = 50, Carrier = 2 });
            index.SetCarrier(2, 1);
            index.SetCarrier(3, 2);
            Evaluate(interest, index, 0);
            CollectionAssert.AreEqual(new ulong[] { 1, 2, 3 }, _entered, "a passenger's spawn names its ship's container");

            index.Move(1, interest.Grid.RegionOf(5000, 0, 0));
            index.Add(1, interest.Grid.RegionOf(5000, 0, 0), new Record { X = 5000 });
            index.Add(2, 0, new Record { X = 5000, Carrier = 1 });
            index.Add(3, 0, new Record { X = 5000, Carrier = 2 });
            Evaluate(interest, index, 1);
            Evaluate(interest, index, 4);
            CollectionAssert.AreEqual(new ulong[] { 3, 2, 1 }, _left);
        }

        /// <summary>
        /// The same rule on a chain far deeper than the 16-hop cap the depth sort used to carry (design D71).
        /// A clamped depth makes every level past the cap compare equal, and the sort that is supposed to put a
        /// container ahead of its contents leaves them in whatever order the scan produced.
        /// </summary>
        [Test]
        public void ADeepChainStillArrivesCarrierFirstAndLeavesContentFirst()
        {
            const int depth = 40;
            var interest = Interest(out var index);
            ulong near = interest.Grid.RegionOf(50, 0, 0);
            for (ulong id = 1; id <= depth; id++)
            {
                index.Add(id, near, new Record { X = 50, Carrier = id == 1 ? 0 : id - 1 });
                if (id > 1) index.SetCarrier(id, id - 1);
            }
            Evaluate(interest, index, 0);
            Assert.AreEqual(depth, _entered.Count, "every level of the chain is in the set");
            for (int i = 1; i < _entered.Count; i++)
                Assert.Less(_entered.IndexOf((ulong)i), _entered.IndexOf((ulong)i + 1),
                    $"#{i + 1} names #{i} as its container, so it cannot arrive first");

            index.Move(1, interest.Grid.RegionOf(5000, 0, 0));
            for (ulong id = 1; id <= depth; id++)
                index.SetValue(id, new Record { X = 5000, Carrier = id == 1 ? 0 : id - 1 });
            Evaluate(interest, index, 1);
            Evaluate(interest, index, 4);
            Assert.AreEqual(depth, _left.Count);
            for (int i = 1; i < _left.Count; i++)
                Assert.Greater(_left.IndexOf((ulong)i), _left.IndexOf((ulong)i + 1),
                    $"#{i + 1} has to be despawned before the #{i} it was standing in");
        }

        [Test]
        public void ABoxFocusCoversItsWholeWindow()
        {
            var interest = Interest(out var index);
            interest.SetFoci(new List<InterestFocus> { InterestFocus.Box(1000, 0, 0, 200, 50, 200) });
            Place(index, interest.Grid, 10, 1090, 0);     // inside the box
            Place(index, interest.Grid, 11, 1150, 0);     // outside it, but within the radius of its face
            Place(index, interest.Grid, 12, 1400, 0);     // beyond the box and the radius
            Evaluate(interest, index, 0);
            CollectionAssert.Contains(_entered, 10UL);
            CollectionAssert.Contains(_entered, 11UL);
            CollectionAssert.DoesNotContain(_entered, 12UL);
        }

        [Test]
        public void AnEntityThatVanishesFromTheIndexLeavesAtOnce()
        {
            var interest = Interest(out var index);
            Place(index, interest.Grid, 10, 50, 0);
            Evaluate(interest, index, 0);
            index.Remove(10);
            Evaluate(interest, index, 0.1);
            CollectionAssert.AreEqual(new ulong[] { 10 }, _left, "nothing will ever announce it again, so there is nothing to linger for");
        }

        [Test]
        public void RemoveTakesAnEntityOutWithoutAnEvaluation()
        {
            var interest = Interest(out var index);
            Place(index, interest.Grid, 10, 50, 0);
            Evaluate(interest, index, 0);
            _left.Clear();
            Assert.IsTrue(interest.Remove(10, _left));
            Assert.IsFalse(interest.Remove(10, _left));
            CollectionAssert.AreEqual(new ulong[] { 10 }, _left);
        }

        [Test]
        public void TheScanFollowsTheFocusAndNotTheWorld()
        {
            var interest = Interest(out var index);
            for (ulong id = 1; id <= 200; id++) Place(index, interest.Grid, id, id * 100, 0);
            Evaluate(interest, index, 0);
            Assert.AreEqual(1, _entered.Count, "of 200 entities spread over 20 km, only the one at the radius is in the set");
            Assert.Less(interest.ScannedRegions, 30, "the scan is bounded by the radius, not by the size of the world");
        }
    }

    /// <summary>
    /// The rotation that decides whose interest is re-evaluated on this tick (design D45). The property that
    /// matters is not "everybody four times a second" — a deadline does that — but "and never all on the same
    /// tick", because the gateway's loop is what a lockstep evaluation stalls.
    /// </summary>
    public class InterestScheduleTests
    {
        private const float EvalHz = 4f;         // one interval = 0.25 s
        private const double Tick = 1.0 / 60;    // a 60 Hz gateway loop
        private static int TicksPerInterval => (int)System.Math.Round(1 / (EvalHz * Tick)); // 15

        private readonly List<ulong> _due = new List<ulong>();

        private static InterestSchedule Populated(int clients)
        {
            var schedule = new InterestSchedule();
            for (ulong id = 1; id <= (ulong)clients; id++) schedule.Add(id);
            Assert.AreEqual(clients, schedule.Count);
            return schedule;
        }

        [Test]
        public void ARoutineTickEvaluatesItsShareAndTheIntervalEvaluatesEverybody()
        {
            const int clients = 300;
            var schedule = Populated(clients);
            var counts = new Dictionary<ulong, int>();

            // One tick first: the whole point is that this is a slice and not the population.
            schedule.Collect(Tick, EvalHz, null, _due);
            Assert.AreEqual(clients / TicksPerInterval, _due.Count, "a tick takes its share: clients / ticks per interval");
            Assert.Less(_due.Count, clients, "and never the whole population at once");

            foreach (ulong id in _due) counts[id] = 1;
            for (int t = 1; t < TicksPerInterval; t++)
            {
                schedule.Collect(Tick, EvalHz, null, _due);
                Assert.LessOrEqual(_due.Count, clients / TicksPerInterval + 1, "every tick stays a slice");
                foreach (ulong id in _due) counts[id] = counts.TryGetValue(id, out int n) ? n + 1 : 1;
            }

            Assert.AreEqual(clients, counts.Count, "and one interval has been round everybody exactly once");
            foreach (var pair in counts) Assert.AreEqual(1, pair.Value, $"client {pair.Key} was evaluated {pair.Value} times in one interval");
        }

        [Test]
        public void TheRateFollowsWallClockTimeAndNotTheTickRate()
        {
            var schedule = Populated(120);
            int total = 0;
            // A slower loop does the same work in fewer, bigger slices: the client-facing rate is the contract,
            // the tick rate is not.
            for (int t = 0; t < 5; t++) { schedule.Collect(0.05, EvalHz, null, _due); total += _due.Count; }
            Assert.AreEqual(120, total, "one interval of wall clock is one sweep, at 20 Hz as at 60 Hz");
        }

        [Test]
        public void ALongStallDoesNotComeBackAndEvaluateEverybodyTwice()
        {
            var schedule = Populated(50);
            schedule.Collect(10.0, EvalHz, null, _due);
            Assert.AreEqual(50, _due.Count, "forty intervals of arrears are still one sweep, not forty");
        }

        [Test]
        public void DirtyClientsAreEvaluatedOnTheNextTickAheadOfTheirTurn()
        {
            var schedule = Populated(300);
            var dirty = new HashSet<ulong> { 250, 251, 252 };
            schedule.Collect(Tick, EvalHz, id => dirty.Contains(id), _due);
            foreach (ulong id in dirty)
                Assert.Contains(id, _due, "a dirty client does not wait for its turn in the rotation");
            Assert.AreEqual(3, schedule.LastDirty);
            Assert.AreEqual(300 / TicksPerInterval, schedule.LastRoutine,
                "and it does not eat the routine slice: the rest of the rotation still gets its turn this tick");
        }

        [Test]
        public void ADirtyStormIsSpreadRatherThanRunInOneTick()
        {
            const int clients = 600;
            var schedule = Populated(clients);
            var pending = new HashSet<ulong>();
            for (ulong id = 1; id <= clients; id++) pending.Add(id);

            schedule.Collect(Tick, EvalHz, pending.Contains, _due);
            int cap = System.Math.Max(InterestSchedule.MinDirtyPerTick, clients / TicksPerInterval * InterestSchedule.DirtyBurst);
            Assert.LessOrEqual(_due.Count, cap, "everything dirty at once is still bounded work on one tick");
            Assert.Greater(_due.Count, 0);

            // And it drains: the dirty cursor moves on, so the same few clients are not served every tick.
            int ticks = 0;
            while (pending.Count > 0 && ticks++ < 200)
            {
                schedule.Collect(Tick, EvalHz, pending.Contains, _due);
                foreach (ulong id in _due) pending.Remove(id);
            }
            Assert.IsEmpty(pending, "and every one of them is evaluated, none starved behind the others");
            Assert.Less(ticks, TicksPerInterval * 2, "well inside two intervals");
        }

        [Test]
        public void ADirtyClientIsNotEvaluatedTwiceOnTheSameTick()
        {
            var schedule = Populated(30);
            schedule.Collect(Tick, EvalHz, _ => true, _due);
            CollectionAssert.AllItemsAreUnique(_due);
            Assert.AreEqual(schedule.LastDirty + schedule.LastRoutine, _due.Count);
        }

        [Test]
        public void LeavingAndJoiningKeepsTheRotationWhole()
        {
            var schedule = Populated(40);
            Assert.IsTrue(schedule.Remove(1));
            Assert.IsFalse(schedule.Remove(1), "removing twice is not two clients gone");
            Assert.IsFalse(schedule.Add(2), "and adding one already in the rotation is not a second turn");
            Assert.AreEqual(39, schedule.Count);

            var seen = new HashSet<ulong>();
            for (int t = 0; t < TicksPerInterval + 1; t++)
            {
                schedule.Collect(Tick, EvalHz, null, _due);
                foreach (ulong id in _due) seen.Add(id);
            }
            Assert.AreEqual(39, seen.Count, "everyone still in the rotation is reached within an interval");
            Assert.IsFalse(seen.Contains(1), "and the one that left is not");

            schedule.Clear();
            schedule.Collect(Tick, EvalHz, null, _due);
            Assert.IsEmpty(_due);
        }
    }
}
