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
}
