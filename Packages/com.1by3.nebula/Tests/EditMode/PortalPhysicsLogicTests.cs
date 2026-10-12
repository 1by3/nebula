using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// The rules of NEB-432 (<c>docs/portals.md</c> §6) that need no physics scene, which EditMode does not have (§14, item 13; the physical tests are in
    /// <c>Tests/PlayMode</c>): which bodies touch a band, how a collider is described and pooled, where a pair of mirrored bodies collides, and the adjacency
    /// relation the ghost band consults.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalPhysicsLogicTests
    {
        private readonly List<Object> _made = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _made) if (o != null) Object.DestroyImmediate(o);
            _made.Clear();
        }

        private T Make<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            _made.Add(go);
            return go.AddComponent<T>();
        }

        // ------------------------------------------------------------------------------------------ the band

        [Test]
        public void ABandIsTheApertureGrownSidewaysAndBandDepthDeepOnEitherSide()
        {
            var band = new PortalBand(PortalAperture.Box(2f, 2.5f), 0.5f, 2f);
            Assert.That(band.HalfWidth, Is.EqualTo(2.5f));
            Assert.That(band.HalfHeight, Is.EqualTo(3f));
            Assert.That(band.Contains(new Vector3(2.5f, -3f, 2f)), Is.True, "its boundary is in");
            Assert.That(band.Contains(new Vector3(2.51f, 0f, 0f)), Is.False);
            Assert.That(band.Contains(new Vector3(0f, 0f, -2.01f)), Is.False);
            Assert.That(band.Contains(new Vector3(0f, 0f, -2.2f), 0.25f), Is.True, "grown by the hysteresis");
            Assert.That(band.Overlaps(new Vector3(2f, -1f, -3f), new Vector3(3f, 1f, -1.9f)), Is.True, "a box that reaches in at a corner");
            Assert.That(band.Overlaps(new Vector3(2.6f, -1f, -1f), new Vector3(3f, 1f, 1f)), Is.False);
        }

        [Test]
        public void TheThroatTakesTheHalfOfTheBandThatIsTheOtherSidesOwn()
        {
            var band = new PortalBand(PortalAperture.Box(2f, 2.5f), 0.5f, 2f);
            var inFront = (min: new Vector3(-1f, -1f, -1.5f), max: new Vector3(1f, 1f, -0.5f));
            var behind = (min: new Vector3(-1f, -1f, 0.5f), max: new Vector3(1f, 1f, 1.5f));
            Assert.That(band.OverlapsHalf(behind.min, behind.max, beyond: true), Is.True);
            Assert.That(band.OverlapsHalf(inFront.min, inFront.max, beyond: true), Is.False);
            Assert.That(band.OverlapsHalf(inFront.min, inFront.max, beyond: false), Is.True);
            Assert.That(band.OverlapsHalf(behind.min, behind.max, beyond: false), Is.False);
        }

        [Test]
        public void AFaceFrameIsTheFacesAxesInTheScenesSpace()
        {
            var frame = new PortalFaceFrame(new Vector3(10f, 2f, 5f), Quaternion.Euler(0f, 90f, 0f));
            var local = frame.ToLocal(new Vector3(10f, 2f, 5f) + Vector3.right * 3f); // the face looks along +x
            Assert.That(local.z, Is.EqualTo(3f).Within(1e-4f));
            Assert.That((frame.ToScene(local) - new Vector3(13f, 2f, 5f)).magnitude, Is.LessThan(1e-4f));
        }

        // ------------------------------------------------------------------------------------------ a pair on opposite sides

        [Test]
        public void APairCollidesInTheSceneOfTheFasterBodyAndKeepsItWhileItTouches()
        {
            Assert.That(PortalPairRule.ResolvesNear(false, false, nearSpeed: 6f, farSpeed: 0f, nearId: 9, farId: 3), Is.True, "the falling body is stopped by the other's copy");
            Assert.That(PortalPairRule.ResolvesNear(false, true, nearSpeed: 0f, farSpeed: 4f, nearId: 3, farId: 9), Is.False);
            Assert.That(PortalPairRule.ResolvesNear(false, false, 0.01f, 0.02f, nearId: 3, farId: 9), Is.True, "a tie goes to the lower id's scene");
            Assert.That(PortalPairRule.ResolvesNear(false, false, 0.01f, 0.02f, nearId: 9, farId: 3), Is.False);
            Assert.That(PortalPairRule.ResolvesNear(true, true, nearSpeed: 0f, farSpeed: 5f, nearId: 3, farId: 9), Is.True,
                "once decided it stays: a body that comes to rest on another does not hand the pair to the other side");
        }

        // ------------------------------------------------------------------------------------------ the pool

        [Test]
        public void ProxiesArePooledByShapeAndSceneSoABodyEnteringABandBuildsNothing()
        {
            var pool = new PortalShapePool<object>();
            var box = new PortalShapeKey(PortalShapeKind.Box, 1f, 1f, 1f, 0, null, true);
            var other = new PortalShapeKey(PortalShapeKind.Box, 1f, 2f, 1f, 0, null, true);
            int made = 0;
            object Make() { made++; return new object(); }

            var first = pool.Acquire(1, box, Make);
            Assert.That(made, Is.EqualTo(1));
            pool.Release(1, box, first);
            Assert.That(pool.Has(1, box), Is.True);
            Assert.That(pool.Acquire(1, box, Make), Is.SameAs(first), "the same shape in the same scene is reused");
            Assert.That(made, Is.EqualTo(1));
            Assert.That(pool.Reused, Is.EqualTo(1));

            pool.Release(1, box, first);
            Assert.That(pool.Acquire(2, box, Make), Is.Not.SameAs(first), "another scene has its own");
            Assert.That(pool.Acquire(1, other, Make), Is.Not.SameAs(first), "and so does another shape");
            Assert.That(made, Is.EqualTo(3));

            pool.Add(1, other, new object());
            Assert.That(pool.FreeCount(1, other), Is.EqualTo(1), "built ahead of need while a link prepares");
            Assert.That(pool.Built, Is.EqualTo(4));
            var drained = new List<object>();
            pool.Drain(drained);
            Assert.That(drained.Count, Is.EqualTo(2));
            Assert.That(pool.Free, Is.EqualTo(0));
        }

        [Test]
        public void AColliderIsDescribedWithItsScaleFoldedIn()
        {
            var box = Make<BoxCollider>("box");
            box.size = new Vector3(1f, 2f, 3f);
            box.center = new Vector3(0f, 1f, 0f);
            box.transform.localScale = new Vector3(2f, 1f, -1f);
            Assert.That(PortalShapes.TryDescribe(box, out var key, out var center, out bool upright), Is.True);
            Assert.That(key.Kind, Is.EqualTo(PortalShapeKind.Box));
            Assert.That((key.A, key.B, key.C), Is.EqualTo((2f, 2f, 3f)));
            Assert.That(center, Is.EqualTo(new Vector3(0f, 1f, 0f)));
            Assert.That(upright, Is.False);

            var twin = Make<BoxCollider>("twin");
            twin.size = new Vector3(2f, 2f, 3f);
            Assert.That(PortalShapes.TryDescribe(twin, out var same, out _, out _), Is.True);
            Assert.That(same.Equals(key), Is.True, "a collider of the same folded shape is interchangeable");

            var sphere = Make<SphereCollider>("sphere");
            sphere.radius = 0.5f;
            sphere.transform.localScale = new Vector3(1f, 3f, 2f);
            Assert.That(PortalShapes.TryDescribe(sphere, out var round, out _, out _), Is.True);
            Assert.That(round.A, Is.EqualTo(1.5f), "a sphere scales by the largest axis");

            var capsule = Make<CapsuleCollider>("capsule");
            capsule.radius = 0.5f;
            capsule.height = 2f;
            capsule.direction = 1;
            capsule.transform.localScale = new Vector3(2f, 1f, 1f);
            Assert.That(PortalShapes.TryDescribe(capsule, out var pill, out _, out _), Is.True);
            Assert.That((pill.A, pill.B, pill.Axis), Is.EqualTo((1f, 2f, (byte)1)));
        }

        [Test]
        public void ACharacterControllerIsACapsuleThatDoesNotTurn()
        {
            var go = new GameObject("pawn");
            _made.Add(go);
            var controller = go.AddComponent<CharacterController>();
            controller.radius = 0.4f;
            controller.height = 1.8f;
            controller.center = new Vector3(0f, 0.9f, 0f);
            Assert.That(PortalShapes.TryDescribe(controller, out var key, out var center, out bool upright), Is.True);
            Assert.That(key.Kind, Is.EqualTo(PortalShapeKind.Capsule));
            Assert.That((key.A, key.B, key.Axis), Is.EqualTo((0.4f, 1.8f, (byte)1)));
            Assert.That(center, Is.EqualTo(new Vector3(0f, 0.9f, 0f)));
            Assert.That(upright, Is.True);
        }

        [Test]
        public void AMeshIsKeyedByTheMeshItselfAndAHoleOfNothingIsNotCopied()
        {
            var mesh = new Mesh();
            _made.Add(mesh);
            var a = Make<MeshCollider>("a");
            var b = Make<MeshCollider>("b");
            a.sharedMesh = mesh;
            b.sharedMesh = mesh;
            Assert.That(PortalShapes.TryDescribe(a, out var ka, out _, out _), Is.True);
            Assert.That(PortalShapes.TryDescribe(b, out var kb, out _, out _), Is.True);
            Assert.That(ka.Equals(kb), Is.True);
            b.sharedMesh = null;
            Assert.That(PortalShapes.TryDescribe(b, out _, out _, out _), Is.False, "no mesh, no shape");
        }

        // ------------------------------------------------------------------------------------------ bodies near a face

        private NetworkIdentity Body(string name, Vector3 at, bool box)
        {
            var go = new GameObject(name);
            _made.Add(go);
            go.transform.position = at;
            if (box) go.AddComponent<BoxCollider>().size = Vector3.one;
            return go.AddComponent<NetworkIdentity>();
        }

        private static readonly PortalFaceFrame Face = new PortalFaceFrame(Vector3.zero, Quaternion.identity);
        private static readonly PortalBand Band = new PortalBand(PortalAperture.Box(2f, 2.5f), 0.5f, 2f);

        [Test]
        public void ABodyIsInTheBandByItsExtentWhenItHasOneByItsCollidersOtherwiseAndByItsRootWhenItHasNeither()
        {
            var bodies = new PortalBodies();
            var rootOnly = Body("root only", new Vector3(0f, 0f, 1.9f), box: false);
            Assert.That(bodies.Touches(rootOnly, bodies.Get(rootOnly, 1), 1, Face, Band, 0f), Is.True, "a point in the band");
            rootOnly.transform.position = new Vector3(0f, 0f, 2.1f);
            Assert.That(bodies.Touches(rootOnly, bodies.Get(rootOnly, 1), 1, Face, Band, 0f), Is.False);

            var crate = Body("crate", new Vector3(0f, 0f, 2.4f), box: true);
            Assert.That(bodies.Touches(crate, bodies.Get(crate, 1), 1, Face, Band, 0f), Is.True, "its root is out, but its box reaches in");
            crate.transform.position = new Vector3(0f, 0f, 2.6f);
            Assert.That(bodies.Touches(crate, bodies.Get(crate, 1), 1, Face, Band, 0f), Is.False);

            // A ship: its root is far from the face, its hull in the doorway.
            var ship = Body("ship", new Vector3(0f, 0f, 12f), box: true);
            ship.GetComponent<BoxCollider>().size = Vector3.one * 0.1f;
            ship.SetExtent(new Bounds(new Vector3(0f, 0f, -11f), new Vector3(2f, 2f, 4f)));
            Assert.That(bodies.Touches(ship, bodies.Get(ship, 1), 1, Face, Band, 0f), Is.True, "by its extent");
            ship.ClearExtent();
            Assert.That(bodies.Touches(ship, bodies.Get(ship, 40), 40, Face, Band, 0f), Is.False, "the small box it has left is nowhere near");
        }

        [Test]
        public void TheCollidersThatReachTheBandAreTheOnesAProxyIsMadeFrom()
        {
            var bodies = new PortalBodies();
            var ship = Body("ship", new Vector3(0f, 0f, 6f), box: false);
            var near = new GameObject("hull near").AddComponent<BoxCollider>();
            var far = new GameObject("hull far").AddComponent<BoxCollider>();
            _made.Add(near.gameObject);
            _made.Add(far.gameObject);
            foreach (var c in new[] { near, far }) { c.transform.SetParent(ship.transform, false); c.size = Vector3.one * 2f; }
            near.transform.localPosition = new Vector3(0f, 0f, -5f);
            far.transform.localPosition = new Vector3(0f, 0f, 5f);
            var eligible = new List<int>();
            var info = bodies.Get(ship, 1);
            Assert.That(bodies.Touches(ship, info, 1, Face, Band, 0f, eligible), Is.True);
            Assert.That(eligible.Count, Is.EqualTo(1));
            Assert.That(info.Colliders[eligible[0]], Is.SameAs(near), "only the part of the hull that is in the doorway");
        }

        // ------------------------------------------------------------------------------------------ adjacency

        [Test]
        public void PortalAdjacencyIsASeparateRelationBetweenTheFacesContainerAndTheFarContainersItsBandTouches()
        {
            var table = new PortalAdjacency();
            var near = ContainerRef.Runtime(1);
            var farA = ContainerRef.Runtime(2);
            var farB = ContainerRef.Runtime(3);
            var elsewhere = ContainerRef.Runtime(4);
            table.Set(10, near, new[] { farA, farB }, driver: "w1");
            var far = new List<ContainerRef>();
            table.FarOf(near, far);
            Assert.That(far, Is.EquivalentTo(new[] { farA, farB }));
            var back = new List<ContainerRef>();
            table.NearOf(farB, back);
            Assert.That(back, Is.EqualTo(new[] { near }));
            Assert.That(table.Relates(near, farA), Is.True);
            Assert.That(table.Relates(near, elsewhere), Is.False, "two containers are adjacent through a portal only if its band touches the far one");
            Assert.That(table.Relates(elsewhere, farA), Is.False);

            table.Set(10, near, new[] { farA });
            far.Clear();
            table.FarOf(near, far);
            Assert.That(far, Is.EqualTo(new[] { farA }), "a link's edge is replaced when its band touches something else");
            Assert.That(table.Remove(10), Is.True);
            Assert.That(table.Relates(near, farA), Is.False, "and gone with the link");
        }

        [Test]
        public void TheRegistryStillMakesNoNeighboursOfContainersOfTwoScopes()
        {
            var hostScope = ContainerRegistry.RegisterRuntime(81001, new Bounds(new Vector3(500f, 5f, 500f), new Vector3(20f, 10f, 20f)), new InstanceContainerInfo { InstanceId = 0 });
            var roomScope = ContainerRegistry.RegisterRuntime(81002, new Bounds(new Vector3(500f, 5f, 500f), new Vector3(20f, 10f, 20f)), new InstanceContainerInfo { InstanceId = 81002 });
            try
            {
                var neighbours = new List<Container>();
                ContainerRegistry.NeighborsOf(hostScope, neighbours);
                Assert.That(neighbours.Contains(roomScope), Is.False, "exactly the same box, and still not adjacent (docs/scoped-chunk-grids.md D11)");
                ContainerRegistry.NeighborsOf(roomScope, neighbours);
                Assert.That(neighbours.Contains(hostScope), Is.False);
            }
            finally
            {
                ContainerRegistry.UnregisterRuntime(81001);
                ContainerRegistry.UnregisterRuntime(81002);
            }
        }

        // ------------------------------------------------------------------------------------------ the editor's check

        [Test]
        public void ValidateInTheEditorFindsAStaticColliderInTheCrossSectionAndPassesAClearTunnel()
        {
            var go = new GameObject("door");
            _made.Add(go);
            go.SetActive(false);
            go.transform.position = new Vector3(5000f, 2.5f, 5000f);
            var portal = go.AddComponent<Portal>();
            portal.Aperture = PortalAperture.Box(2f, 2.5f);
            go.SetActive(true);
            var floor = new GameObject("floor").AddComponent<BoxCollider>();
            _made.Add(floor.gameObject);
            floor.size = new Vector3(20f, 1f, 20f);
            floor.transform.position = new Vector3(5000f, -0.5f, 5002f); // its top meets the bottom of the opening: not in the way
            Physics.SyncTransforms();
            Assert.That(portal.Validate(), Is.True, "a floor under the doorway is not an obstruction");

            var stone = new GameObject("stone").AddComponent<BoxCollider>();
            _made.Add(stone.gameObject);
            stone.transform.position = new Vector3(5000f, 2.5f, 5001f);
            Physics.SyncTransforms();
            var blockers = new List<Collider>();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("stone"));
            Assert.That(portal.Validate(blockers), Is.False);
            Assert.That(blockers, Is.EqualTo(new[] { stone }));
        }

        // ------------------------------------------------------------------------------------------ small things

        [Test]
        public void ThePortalLayerAndTheBoundsHaveTheirDefaults()
        {
            var config = ScriptableObject.CreateInstance<NebulaConfig>();
            _made.Add(config);
            Assert.That(config.PortalProxyLayer, Is.EqualTo(31));
            var go = new GameObject("portal");
            _made.Add(go);
            go.SetActive(false);
            var portal = go.AddComponent<Portal>();
            Assert.That(portal.MaxProxiesPerPortal, Is.EqualTo(64));
            Assert.That(portal.ObservationOnly, Is.False);
            Assert.That(portal.ProxyCount, Is.EqualTo(0));
            Assert.That(PortalProxy.Is(null), Is.False);
        }

        [Test]
        public void AnAdjacencyMessageRoundTripsAndAnObservationOnlyRecordSaysSo()
        {
            var record = new PortalRecord { PortalId = 5, Id = "d", HalfWidth = 2f, HalfHeight = 2.5f, BandMargin = 0.5f, BandDepth = 2f, Flags = PortalRecord.ObservationOnlyFlag };
            Assert.That(record.ObservationOnly, Is.True);
            var message = new PortalAdjacencyMsg
            {
                LinkId = 77, Generation = 3, State = PortalState.Open, FarScope = 91, Portal = record,
                NearContainer = ContainerRef.Runtime(8), FarContainers = new List<ContainerRef> { ContainerRef.Runtime(9), ContainerRef.Runtime(10) },
            };
            var w = new NetworkWriter();
            message.Write(w);
            var r = new NetworkReader(w.ToSegment());
            Assert.That((MsgId)r.ReadByte(), Is.EqualTo(MsgId.PortalAdjacency));
            var read = PortalAdjacencyMsg.Read(r);
            Assert.That(r.Remaining, Is.EqualTo(0));
            Assert.That((read.LinkId, read.Generation, read.State, read.FarScope), Is.EqualTo((77UL, 3U, PortalState.Open, 91UL)));
            Assert.That(read.Portal.ObservationOnly, Is.True);
            Assert.That(read.NearContainer, Is.EqualTo(ContainerRef.Runtime(8)));
            Assert.That(read.FarContainers, Is.EqualTo(new[] { ContainerRef.Runtime(9), ContainerRef.Runtime(10) }));

            w.Reset();
            new PortalAdjacencyMsg { Removed = true, LinkId = 77 }.Write(w);
            r = new NetworkReader(w.ToSegment());
            r.ReadByte();
            var gone = PortalAdjacencyMsg.Read(r);
            Assert.That((gone.Removed, gone.LinkId), Is.EqualTo((true, 77UL)));
        }

        // ------------------------------------------------------------------------------------------ a side in a physics frame

        [Test]
        public void AFaceFrameIsTheSameWhenItMovedByLessThanTheNoiseOfWorkingItOutAgain()
        {
            var a = new PortalFaceFrame(new Vector3(100f, 2.5f, -40f), Quaternion.Euler(0f, 90f, 0f));
            Assert.That(a.Valid, Is.True);
            Assert.That(a.SameAs(new PortalFaceFrame(new Vector3(100.001f, 2.4995f, -40f), Quaternion.Euler(0f, 90f, 0f))), Is.True, "a millimeter is rounding, not a move");
            Assert.That(a.SameAs(new PortalFaceFrame(new Vector3(101f, 2.5f, -40f), Quaternion.Euler(0f, 90f, 0f))), Is.False, "a metre is a move (a frame's origin shifted)");
            Assert.That(a.SameAs(new PortalFaceFrame(new Vector3(100f, 2.5f, -40f), Quaternion.Euler(0f, 90.1f, 0f))), Is.False, "and so is a turn");
            Assert.That(a.SameAs(default), Is.False, "a face that could not be worked out is not the same as one that could");
            Assert.That(default(PortalFaceFrame).Valid, Is.False);
        }

        [Test]
        public void ASideThatIsNotInAFrameHasItsFaceInTheScopesOwnSceneAndBodiesPoseThroughUnchanged()
        {
            var face = new PortalFace(0UL, "", new Double3(20.0, 2.5, 20.0), Quaternion.Euler(0f, 30f, 0f));
            var frame = PortalSpaces.FaceFrame(face, null);
            Assert.That(frame.Valid, Is.True);
            Assert.That(frame.Origin, Is.EqualTo(ContainerRegistry.ToFrame(face.Origin, 0UL)));
            Assert.That(frame.Rotation, Is.EqualTo(face.Rotation));
            var point = new Vector3(1f, 2f, 3f);
            Assert.That(PortalSpaces.Point(point, null, null), Is.EqualTo(point));
            Assert.That(PortalSpaces.Rotation(Quaternion.identity, null, null), Is.EqualTo(Quaternion.identity));
            Assert.That(PortalSpaces.SpaceOf(null), Is.Null);
            Assert.That(PortalSpaces.SceneOf(null).IsValid(), Is.False);
        }
    }
}
