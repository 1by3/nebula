using System.Collections.Generic;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// Region spaces (<c>docs/container-tree.md</c> D18): a physics frame with regions of its own buckets what is on it
    /// under keys salted with the frame, a focus sees only entities of its own space, and a scope's keys are unchanged.
    /// Pure: no worker, no gateway.
    /// </summary>
    public sealed class FrameRegionSpaceTests
    {
        private struct Record { public double X, Y, Z; public ulong Space; }

        private struct Source : IInterestSource<Record>
        {
            public void Describe(ulong id, in Record value, out InterestEntity entity) =>
                entity = new InterestEntity { NetId = id, X = value.X, Y = value.Y, Z = value.Z, Space = value.Space };
            public bool Authorize(in InterestClient client, in InterestEntity entity) => true;
        }

        [Test]
        public void FrameKeysAreStableDistinctAndSeparateFromScopes()
        {
            ulong planet = RegionKeys.FrameKeyOf(ContainerRef.Dynamic(42));
            Assert.AreEqual(planet, RegionKeys.FrameKeyOf(ContainerRef.Dynamic(42)));
            Assert.AreNotEqual(planet, RegionKeys.FrameKeyOf(ContainerRef.Dynamic(43)));
            Assert.AreNotEqual(planet, RegionKeys.FrameKeyOf(ContainerRef.Runtime(42)), "a carried and a runtime container never share a key");
            Assert.AreEqual(0UL, RegionKeys.FrameKeyOf(ContainerRef.None));

            ulong region = InterestGrid.PackRegion(3, 0, 7);
            Assert.AreEqual(RegionKeys.Salt(region, 5), RegionKeys.Salt(region, 5, 0), "the scope's own space is keyed as before");
            ulong framed = RegionKeys.Salt(region, 5, planet);
            Assert.AreNotEqual(RegionKeys.Salt(region, 5), framed);
            Assert.AreNotEqual(RegionKeys.Salt(region, 0, planet), framed, "a frame in another scope is another space");
            Assert.AreEqual(region, RegionKeys.Unsalt(framed, 5, planet));
        }

        [Test]
        public void AFocusSeesOnlyEntitiesOfItsOwnSpace()
        {
            var settings = InterestSettings.Default;
            settings.Radius = 100;
            settings.CellSize = 64;
            var grid = InterestGrid.Resolve(settings);
            ulong planet = RegionKeys.FrameKeyOf(ContainerRef.Dynamic(9));
            var index = new InterestIndex<Record>();
            // Two entities at the same coordinates: one in the scope's space, one on the planet.
            index.Add(1, RegionKeys.Salt(grid.RegionOf(10, 0, 10), 0, 0), new Record { X = 10, Z = 10 });
            index.Add(2, RegionKeys.Salt(grid.RegionOf(10, 0, 10), 0, planet), new Record { X = 10, Z = 10, Space = planet });
            var interest = new ClientInterest<Record, Source>(new Source { })
            {
                Settings = settings,
                Grid = grid,
                Client = new InterestClient { ClientId = 1, HasPawn = true, PawnNetId = 99, PawnSpace = planet },
            };
            var entered = new List<ulong>();
            var left = new List<ulong>();

            interest.SetFoci(new List<InterestFocus> { InterestFocus.Point(0, 0, 0, 1f, 99, planet) });
            interest.Evaluate(index, 0, entered, left);
            CollectionAssert.AreEquivalent(new[] { 2UL }, entered, "a focus on the planet scans and measures the planet's space only");

            entered.Clear();
            interest.SetFoci(new List<InterestFocus> { InterestFocus.Point(0, 0, 0, 1f, 99, planet), InterestFocus.Point(0, 0, 0, 1f, 99, 0) });
            interest.Evaluate(index, 1, entered, left);
            CollectionAssert.AreEquivalent(new[] { 1UL }, entered, "a second focus in the space around the planet adds what is there");
        }
    }
}
