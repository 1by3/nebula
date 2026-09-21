using System.Collections.Generic;
using Nebula.World;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The worker's interest decisions (design §5, §6), exercised through the pure helpers the publish path is
    /// built from: where an entity belongs in the index, which gateways hear about it, who is told what when it
    /// rebuckets, and that a floating-origin shift changes no key.
    /// </summary>
    public sealed class WorkerInterestTests
    {
        private static InterestSettings Settings => new InterestSettings
        {
            Radius = 120f,
            ExitMargin = 16f,
            MaxRadius = 1024f,
            CellSize = 64f,
            Planar = true,
            EvalHz = 4f,
        };

        // ------------------------------------------------------------------------------------------- placement

        [Test]
        public void APrefabWithoutAnOverrideIsBucketedByRegionAtTheMeshRadius()
        {
            var placement = NebulaWorker.PlacementOf(false, 0f, Settings, out float radius);
            Assert.AreEqual(InterestPlacement.Region, placement);
            Assert.AreEqual(120f, radius);
        }

        [Test]
        public void ARadiusLargerThanTheMeshRadiusGoesInTheWideList()
        {
            var placement = NebulaWorker.PlacementOf(false, 400f, Settings, out float radius);
            Assert.AreEqual(InterestPlacement.Wide, placement, "a region scan would never find it");
            Assert.AreEqual(400f, radius);
        }

        [Test]
        public void ARadiusInsideTheMeshRadiusStaysARegionEntity()
        {
            Assert.AreEqual(InterestPlacement.Region, NebulaWorker.PlacementOf(false, 50f, Settings, out float radius));
            Assert.AreEqual(50f, radius);
        }

        [Test]
        public void ARelevanceRadiusIsClampedToTheConfiguredCeiling()
        {
            NebulaWorker.PlacementOf(false, 99999f, Settings, out float radius);
            Assert.AreEqual(Settings.MaxRadius, radius);
        }

        [Test]
        public void AlwaysRelevantWinsOverAnyRadius()
        {
            Assert.AreEqual(InterestPlacement.Global, NebulaWorker.PlacementOf(true, 0f, Settings, out _));
            Assert.AreEqual(InterestPlacement.Global, NebulaWorker.PlacementOf(true, 5000f, Settings, out _));
        }

        // ------------------------------------------------------------------------------------------- masks

        [Test]
        public void ARegionEntityReachesTheGatewaysSubscribingItsRegion()
        {
            ulong mask = NebulaWorker.EffectiveMask(InterestPlacement.Region, regionMask: 0b0101, wideMask: 0b1000, linkedMask: 0b1111, stickyMask: 0);
            Assert.AreEqual(0b0101UL, mask);
        }

        [Test]
        public void AWideEntityReachesTheGatewaysItsOwnRadiusCovers()
        {
            ulong mask = NebulaWorker.EffectiveMask(InterestPlacement.Wide, regionMask: 0b0001, wideMask: 0b1000, linkedMask: 0b1111, stickyMask: 0);
            Assert.AreEqual(0b1000UL, mask, "a wide entity is matched against foci, not found by a region scan");
        }

        [Test]
        public void AGlobalEntityReachesEveryLink()
        {
            ulong mask = NebulaWorker.EffectiveMask(InterestPlacement.Global, regionMask: 0, wideMask: 0, linkedMask: 0b1011, stickyMask: 0);
            Assert.AreEqual(0b1011UL, mask);
        }

        [Test]
        public void OwnerAndExplicitSubscribersAreAddedWhateverTheRegionSays()
        {
            // Bit 3 is the owner's gateway or an explicit subscriber; it hears about the entity in any region.
            ulong mask = NebulaWorker.EffectiveMask(InterestPlacement.Region, regionMask: 0b0001, wideMask: 0, linkedMask: 0b1111, stickyMask: 0b1000);
            Assert.AreEqual(0b1001UL, mask);
        }

        // ------------------------------------------------------------------------------------------- rebucketing

        [Test]
        public void RebucketingSpawnsIntoTheNewRegionAndForgetsTheOld()
        {
            NebulaWorker.RebucketBits(fromMask: 0b0011, toMask: 0b0110, stickyMask: 0, out ulong spawn, out ulong forget);
            Assert.AreEqual(0b0100UL, spawn, "only the gateway that did not have it");
            Assert.AreEqual(0b0001UL, forget, "only the gateway that loses it");
        }

        [Test]
        public void AGatewaySubscribingBothRegionsIsToldNothing()
        {
            NebulaWorker.RebucketBits(0b0010, 0b0010, 0, out ulong spawn, out ulong forget);
            Assert.AreEqual(0UL, spawn);
            Assert.AreEqual(0UL, forget);
        }

        [Test]
        public void TheOwnersGatewayIsNeverToldToForgetItsOwnPawn()
        {
            // Bit 0 speaks for the owner and subscribes only the region being left: it still keeps the entity.
            NebulaWorker.RebucketBits(fromMask: 0b0001, toMask: 0b0010, stickyMask: 0b0001, out ulong spawn, out ulong forget);
            Assert.AreEqual(0b0010UL, spawn);
            Assert.AreEqual(0UL, forget, "a sticky subscriber keeps the entity regardless of region");
        }

        [Test]
        public void AnExplicitSubscriberIsNotSpawnedTheEntityTwice()
        {
            // Bit 2 already follows the entity by name, so entering its region must not re-announce it.
            NebulaWorker.RebucketBits(fromMask: 0, toMask: 0b0100, stickyMask: 0b0100, out ulong spawn, out ulong forget);
            Assert.AreEqual(0UL, spawn);
            Assert.AreEqual(0UL, forget);
        }

        // ------------------------------------------------------------------------------------------- origin

        [Test]
        public void AFloatingOriginShiftChangesNoRegionKey()
        {
            var definition = ScriptableObject.CreateInstance<WorldDefinition>();
            definition.CellSize = Vector3.one * 512;
            NebulaWorld.LoadRuntime(definition);
            try
            {
                var grid = InterestGrid.Resolve(Settings, 512, cellsCentred: false);
                var probe = new GameObject("probe");
                probe.transform.position = new Vector3(37.5f, 2f, -93.25f);
                ulong before = NebulaWorker.RegionOfFrame(grid, WorldOrigin.Cell, definition.CellSize, probe.transform.position);

                // Shift the origin the way the streamer does; the object moves with the frame.
                var from = WorldOrigin.Cell;
                var to = new Vector3Int(1000, 0, -4000);
                NebulaWorld.Streamer.ShiftOrigin(to);
                probe.transform.position += WorldOrigin.ShiftDelta(definition, from, WorldOrigin.Cell);

                ulong after = NebulaWorker.RegionOfFrame(grid, WorldOrigin.Cell, definition.CellSize, probe.transform.position);
                Assert.AreEqual(before, after, "a region key is made from absolute coordinates, so the origin cannot move it");
                Object.DestroyImmediate(probe);
            }
            finally
            {
                NebulaWorld.Unload();
                Object.DestroyImmediate(definition);
            }
        }

        [Test]
        public void TheSameAbsolutePointInDifferentFramesGivesOneKey()
        {
            var grid = InterestGrid.Resolve(Settings, 512, cellsCentred: false);
            var cellSize = Vector3.one * 512;
            // Absolute (512*3 + 10, 0, 512*-2 + 5), expressed from two different origin cells.
            ulong a = NebulaWorker.RegionOfFrame(grid, new Vector3Int(3, 0, -2), cellSize, new Vector3(10, 0, 5));
            ulong b = NebulaWorker.RegionOfFrame(grid, new Vector3Int(0, 0, 0), cellSize, new Vector3(512 * 3 + 10, 0, 512 * -2 + 5));
            Assert.AreEqual(a, b);
        }

        // ------------------------------------------------------------------------------------------- ghost band

        [Test]
        public void AGhostTargetLapsesWhenTheEntityTheLinkOrTheLingerGoes()
        {
            Assert.IsFalse(NebulaWorker.GhostTargetExpired(true, true, now: 10f, lastSeen: 9.5f, lingerSeconds: 2f));
            Assert.IsTrue(NebulaWorker.GhostTargetExpired(false, true, 10f, 9.5f, 2f), "we do not own it any more");
            Assert.IsTrue(NebulaWorker.GhostTargetExpired(true, false, 10f, 9.5f, 2f), "the peer is gone");
            Assert.IsTrue(NebulaWorker.GhostTargetExpired(true, true, 10f, 7.5f, 2f), "out of the band for longer than the linger");
        }

        [Test]
        public void AFreshlySeededTargetSurvivesTheSameTicksExpiryPass()
        {
            // What SeedInheritedTargets guarantees after a handover: the new owner's first band pass must not
            // despawn (and immediately respawn) a copy the neighbour never lost.
            const float now = 100f;
            Assert.IsFalse(NebulaWorker.GhostTargetExpired(true, true, now, lastSeen: now, lingerSeconds: 2f));
        }

        // ------------------------------------------------------------------------------------------- index

        [Test]
        public void CarriedEntitiesBucketWithTheirRootCarrier()
        {
            var index = new InterestIndex<string>();
            var grid = new InterestGrid(64);
            ulong shipRegion = grid.RegionOf(10, 0, 10);
            ulong farRegion = grid.RegionOf(1000, 0, 1000);

            index.Add(1, shipRegion, "ship");
            index.Add(2, farRegion, "seat");   // its own position says another region entirely
            index.Add(3, farRegion, "pawn");
            index.SetCarrier(2, 1);
            index.SetCarrier(3, 2);            // a pawn in a seat in a ship

            Assert.AreEqual(3, index.Region(shipRegion).Count, "the whole subtree rides with the carrier");
            Assert.AreEqual(0, index.Region(farRegion).Count);

            ulong next = grid.RegionOf(500, 0, 500);
            Assert.IsTrue(index.Move(1, next));
            Assert.AreEqual(3, index.Region(next).Count, "moving the ship moves everything aboard as one unit");
            Assert.IsFalse(index.Move(3, farRegion), "a passenger cannot leave its carrier's bucket on its own");
        }

        [Test]
        public void WideAndGlobalEntitiesAreNotInAnyRegionBucket()
        {
            var index = new InterestIndex<string>();
            var grid = new InterestGrid(64);
            ulong region = grid.RegionOf(0, 0, 0);
            index.Add(1, region, "crate");
            index.AddWide(2, "dirigible");
            index.AddGlobal(3, "weather");
            Assert.AreEqual(1, index.Region(region).Count);
            Assert.AreEqual(1, index.WideCount);
            Assert.AreEqual(1, index.GlobalCount);
            Assert.AreEqual(3, index.Count);
        }

        // ------------------------------------------------------------------------------------------- telemetry

        [Test]
        public void TheInterestBlockOfATelemetryDocumentIsReadBack()
        {
            const string json = "{\"worker\":\"w1\",\"index\":1,\"detail\":false," +
                "\"interest\":{\"regions\":12,\"filterMs\":0.35,\"global\":true,\"entriesSent\":400,\"entriesTotal\":1000," +
                "\"bytesSent\":2048,\"bytesUnfiltered\":8192,\"warning\":\"partition this container\"," +
                "\"gateways\":[{\"id\":\"g1\",\"regions\":7},{\"id\":\"g2\",\"regions\":5}]}," +
                "\"containers\":[{\"id\":\"arena\",\"players\":3,\"bots\":0,\"serverDriven\":0,\"other\":1,\"ghosts\":2}]}";

            Assert.IsTrue(MeshTelemetry.ParseInterest(json, out var interest));
            Assert.AreEqual(12, interest.Regions);
            Assert.AreEqual(0.35, interest.FilterMs, 1e-6);
            Assert.IsTrue(interest.Global);
            Assert.AreEqual(400, interest.EntriesSent);
            Assert.AreEqual(1000, interest.EntriesTotal);
            Assert.AreEqual(2048, interest.BytesSent);
            Assert.AreEqual(8192, interest.BytesUnfiltered);
            Assert.AreEqual(2, interest.Gateways);
            Assert.AreEqual("partition this container", interest.Warning);

            // The nested gateway array must not confuse the container parse that runs over the same document.
            var containers = new List<KeyValuePair<string, ContainerLoad>>();
            MeshTelemetry.ParseContainers(json, containers);
            Assert.AreEqual(1, containers.Count);
            Assert.AreEqual("arena", containers[0].Key);
            Assert.AreEqual(3, containers[0].Value.Players);
        }

        [Test]
        public void ADocumentWithoutAnInterestBlockIsNotMisread()
        {
            Assert.IsFalse(MeshTelemetry.ParseInterest("{\"worker\":\"w1\",\"containers\":[]}", out var interest));
            Assert.AreEqual(0, interest.Regions);
        }

        // ------------------------------------------------------------------------------------------- publisher

        [Test]
        public void GroupingProducesOneGroupPerDistinctSubscriberSet()
        {
            var publisher = new RegionPublisher();
            var index = new InterestIndex<string>();
            var grid = new InterestGrid(64);
            ulong a = grid.RegionOf(0, 0, 0), b = grid.RegionOf(100, 0, 0), c = grid.RegionOf(500, 0, 0);
            index.Add(1, a, "a1");
            index.Add(2, a, "a2");
            index.Add(3, b, "b1");
            index.Add(4, c, "c1");
            publisher.Subscribe(0, a);
            publisher.Subscribe(1, a);
            publisher.Subscribe(1, b);
            publisher.BuildGroups(index);

            var masks = new List<ulong>();
            foreach (var group in publisher.Groups) masks.Add(group.Mask);
            CollectionAssert.AreEquivalent(new ulong[] { 0b11, 0b10 }, masks);
            Assert.AreEqual(3, publisher.FilteredEntities, "the entity in the region nobody subscribes is not sent");
            Assert.AreEqual(4, publisher.TotalEntities);
        }
    }
}
