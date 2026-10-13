using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-433 (<c>docs/portals.md</c> D33): a portal query with a tick poses the far window's entities at the pose the history keeps for it, finds an entity that
    /// crossed during the rewound interval in the scope it was in then, and allocates nothing once its shapes are pooled. Same street, door and hall as
    /// <see cref="PortalQueriesPlayTests"/>.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalQueriesRewindPlayTests
    {
        private PortalPhysicsRig _rig;
        private int _history;
        private PortalHit[] _hits;
        private readonly PortalQueryOptions _options = new PortalQueryOptions();

        private PortalHarness H => _rig.H;
        private PortalQueries Q => H.W.Instance.PortalQueries;
        private static PortalAsker PartyA => PortalAsker.OfParty(PortalHarness.Owner);

        [SetUp]
        public void SetUp()
        {
            _history = StateHistory.WindowTicks;
            _rig = new PortalPhysicsRig();
            StateHistory.WindowTicks = 300; // the mesh has no Initialize(), which is what reads the config knob
            _hits = new PortalHit[16];
            _options.Tick = 0;
            _options.RewindNear = false;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _rig.Dispose();
            StateHistory.WindowTicks = _history;
            yield return null;
            yield return null;
        }

        private static Vector3 Street(float x, float y, float z) => new Vector3(20f + x, y, 20f + z);

        private NetworkIdentity HallCrate(Vector3 at)
        {
            var crate = H.W.SpawnServerDriven(_rig.CratePrefab, H.Hall, at, Quaternion.identity);
            crate.GetComponent<BoxCollider>().sharedMaterial = PortalPhysicsRig.Slippery();
            return crate;
        }

        private static void Teleport(NetworkIdentity entity, Vector3 to)
        {
            var body = entity.GetComponent<Rigidbody>();
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            entity.transform.position = to;
            body.position = to;
        }

        // ------------------------------------------------------------------------------------------ the far leg at its rewound pose

        [UnityTest]
        public IEnumerator AFarEntityIsHitAtItsRewoundPoseWithATickAndAtItsCurrentOneWithout()
        {
            _rig.OpenDoor();
            var crate = HallCrate(Street(0f, 0.5f, 4f));
            _rig.Steps(30);
            uint then = H.TickNumber;
            Teleport(crate, Street(1.5f, 0.5f, 4f));
            _rig.Steps(10);
            Assert.That(crate.StateAt(then).Available, Is.True, "the ring answers for it");
            Assert.That(crate.StateAt(then).Position.x, Is.EqualTo(20f).Within(0.05f));

            // Now: the crate is 1.5 m to the right. A ray at x = 20 meets the hall's back wall, one at x = 22.5 the crate.
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var nowAtOld), Is.True);
            Assert.That(nowAtOld.Entity, Is.Null);
            Assert.That(nowAtOld.Collider.name, Is.EqualTo("hall wall"));
            Assert.That(Q.Raycast(PartyA, 0UL, Street(1.5f, 0.5f, -10f), Vector3.forward, 40f, out var nowAtNew), Is.True);
            Assert.That(nowAtNew.Entity, Is.SameAs(crate));
            Assert.That(nowAtNew.Flags & PortalHitFlags.Rewound, Is.EqualTo(PortalHitFlags.None));

            // Then: the other way round.
            _options.Tick = then;
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var thenAtOld, _options), Is.True);
            Assert.That(thenAtOld.Entity, Is.SameAs(crate), "where it was");
            Assert.That(thenAtOld.Flags, Is.EqualTo(PortalHitFlags.ThroughPortal | PortalHitFlags.Rewound));
            Assert.That(thenAtOld.PointInScope.z, Is.EqualTo(23.5f).Within(0.02f));
            Assert.That(thenAtOld.PointInScope.x, Is.EqualTo(20f).Within(0.02f));
            Assert.That(Q.Raycast(PartyA, 0UL, Street(1.5f, 0.5f, -10f), Vector3.forward, 40f, out var thenAtNew, _options), Is.True);
            Assert.That(thenAtNew.Entity, Is.Null, "not where it is");
            Assert.That(thenAtNew.Collider.name, Is.EqualTo("hall wall"));

            // Everything is back where it was: the crate is at its present pose, nothing of the rewind is left in the scene.
            Assert.That(crate.transform.position.x, Is.EqualTo(21.5f).Within(0.05f));
            Assert.That(Q.Raycast(PartyA, 0UL, Street(1.5f, 0.5f, -10f), Vector3.forward, 40f, out var after), Is.True);
            Assert.That(after.Entity, Is.SameAs(crate));
            yield break;
        }

        [UnityTest]
        public IEnumerator ATickAloneLeavesTheNearScopeToTheGameAndRewindNearDoesBoth()
        {
            _rig.OpenDoor();
            var crate = _rig.Crate(Street(0f, 0.5f, -6f), PortalPhysicsRig.Slippery());
            _rig.Steps(30);
            uint then = H.TickNumber;
            Teleport(crate, Street(3f, 0.5f, -6f));
            _rig.Steps(10);
            _options.Tick = then;
            // The near leg is not rewound by default: the crate is not on the ray where it was.
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var plain, _options), Is.True);
            Assert.That(plain.Entity, Is.Null);
            _options.RewindNear = true;
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var both, _options), Is.True);
            Assert.That(both.Entity, Is.SameAs(crate));
            Assert.That(both.Flags, Is.EqualTo(PortalHitFlags.Rewound));
            Assert.That(both.Distance, Is.EqualTo(3.5f).Within(0.03f));
            yield break;
        }

        // ------------------------------------------------------------------------------------------ crossed during the interval

        [UnityTest]
        public IEnumerator AnEntityThatCrossedIntoTheHallIsFoundInTheStreetAtATickBeforeItDid()
        {
            _rig.OpenDoor();
            var crate = _rig.Crate(Street(0f, 0.5f, -3f), PortalPhysicsRig.Slippery());
            var body = PortalPhysicsRig.BodyOf(crate);
            body.linearVelocity = new Vector3(0f, 0f, 2f);
            for (int i = 0; i < 400 && crate.transform.position.z < 20f - 1.2f; i++) _rig.Step();
            uint then = H.TickNumber;
            Assert.That(crate.InstanceId, Is.EqualTo(0UL), "not yet");
            var before = crate.StateAt(then);
            Assert.That(before.Available, Is.True);
            for (int i = 0; i < 400 && crate.InstanceId == 0UL; i++) _rig.Step();
            _rig.Steps(60); // clear of the plane, so that a ray from the street does not start inside it
            Assert.That(crate.InstanceId, Is.EqualTo(PortalHarness.Private), "it crossed since");
            Assert.That(crate.transform.position.z, Is.GreaterThan(20f + 0.6f));
            Assert.That(crate.StateAt(then).Container, Is.SameAs(H.Hall), "the ring was rewritten into the hall (D18)");

            // Live, it is in the hall: the street is empty at the old place and the far leg finds it.
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var live), Is.True);
            Assert.That(live.Entity, Is.SameAs(crate));
            Assert.That(live.Scope, Is.EqualTo(PortalHarness.Private));

            // At the tick, it was in the street: with a tick the far leg does not find it in the hall, where its ring would put it, and the near leg finds it in the street.
            _options.Tick = then;
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var far, _options), Is.True);
            Assert.That(far.Entity, Is.Not.SameAs(crate), "not in the hall then");
            _options.RewindNear = true;
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var near, _options), Is.True);
            Assert.That(near.Entity, Is.SameAs(crate), "in the street then");
            Assert.That(near.Scope, Is.EqualTo(0UL));
            Assert.That(near.Flags, Is.EqualTo(PortalHitFlags.Rewound));
            Assert.That(near.PointInScope.z, Is.EqualTo(before.Position.z - 0.5f).Within(0.03f), "on the face it showed then");
            yield break;
        }

        [UnityTest]
        public IEnumerator AnEntityThatCrossedOutOfTheHallIsFoundInTheHallAtATickBeforeItDid()
        {
            _rig.OpenDoor();
            var crate = HallCrate(Street(0f, 0.5f, 4f));
            _rig.Steps(30);
            var body = PortalPhysicsRig.BodyOf(crate);
            body.linearVelocity = new Vector3(0f, 0f, -2f);
            for (int i = 0; i < 400 && crate.transform.position.z > 20f + 1.2f; i++) _rig.Step();
            uint then = H.TickNumber;
            Assert.That(crate.InstanceId, Is.EqualTo(PortalHarness.Private), "still in the hall");
            var before = crate.StateAt(then);
            for (int i = 0; i < 400 && crate.InstanceId != 0UL; i++) _rig.Step();
            _rig.Steps(3);
            Assert.That(crate.InstanceId, Is.EqualTo(0UL), "it crossed back out since");
            // Out of the way of the near leg, in the street.
            Teleport(crate, Street(8f, 0.5f, -5f));
            _rig.Steps(3);

            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var live), Is.True);
            Assert.That(live.Collider.name, Is.EqualTo("hall wall"), "now: nothing of the crate in the hall");
            _options.Tick = then;
            Assert.That(Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out var hit, _options), Is.True);
            Assert.That(hit.Entity, Is.SameAs(crate), "then: in the hall, though it is in the street now");
            Assert.That(hit.Scope, Is.EqualTo(PortalHarness.Private));
            Assert.That(hit.Flags, Is.EqualTo(PortalHitFlags.ThroughPortal | PortalHitFlags.Rewound));
            Assert.That(hit.Collider, Is.SameAs(crate.GetComponent<BoxCollider>()), "the entity's own collider is reported, never the shape stood in for it");
            Assert.That(hit.PointInScope.z, Is.EqualTo(before.Position.z - 0.5f).Within(0.03f));
            Assert.That(PortalProxy.Is(hit.Collider), Is.False);
            // And nothing is left standing in the hall.
            Assert.That(UnityEngine.Object.FindObjectsByType<RewindShadow>(FindObjectsInactive.Exclude).Length, Is.EqualTo(0));
            yield break;
        }

        // ------------------------------------------------------------------------------------------ the helper

        [UnityTest]
        public IEnumerator HistoryRewindPutsAScopeBackAsItWasAndThenPutsItBack()
        {
            _rig.OpenDoor();
            var crate = _rig.Crate(Street(0f, 0.5f, -6f), PortalPhysicsRig.Slippery());
            _rig.Steps(30);
            uint then = H.TickNumber;
            var thenPosition = crate.transform.position;
            Teleport(crate, Street(3f, 0.5f, -6f));
            _rig.Steps(10);
            var nowPosition = crate.transform.position;
            var street = Physics.defaultPhysicsScene;
            Assert.That(street.Raycast(Street(0f, 0.5f, -10f), Vector3.forward, 8f), Is.False, "now: nothing there");

            using (var rewind = HistoryRewind.Begin(H.W.Instance, 0UL, then, new Bounds(Street(0f, 0.5f, -6f), Vector3.one * 8f)))
            {
                Assert.That(rewind.Posed, Is.GreaterThanOrEqualTo(1));
                Assert.That(street.Raycast(Street(0f, 0.5f, -10f), Vector3.forward, out var raw, 8f), Is.True, "rewound: a plain cast finds it");
                Assert.That(raw.collider.GetComponentInParent<NetworkIdentity>(), Is.SameAs(crate));
                Assert.That(crate.transform.position, Is.EqualTo(thenPosition).Using(new UnityEngine.TestTools.Utils.Vector3EqualityComparer(0.05f)));
            }
            Assert.That(crate.transform.position, Is.EqualTo(nowPosition), "put back exactly");
            Assert.That(street.Raycast(Street(0f, 0.5f, -10f), Vector3.forward, 8f), Is.False);
            yield break;
        }

        // ------------------------------------------------------------------------------------------ cost

        [UnityTest]
        public IEnumerator AQueryAllocatesNothingOnceItsShapesArePooled()
        {
            _rig.OpenDoor();
            var street = _rig.Crate(Street(0f, 0.5f, -6f), PortalPhysicsRig.Slippery());
            var hall = HallCrate(Street(0f, 0.5f, 4f));
            _rig.Steps(30);
            uint then = H.TickNumber;
            _rig.Steps(5);
            _options.Tick = then;
            _options.RewindNear = true;
            // Out of the way: both legs are rewound (poses, no shadows) and the crate in the hall is crossed in effect by nothing.
            for (int i = 0; i < 20; i++) Run();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++) Run();
            long used = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(used, Is.LessThan(2048), $"200 queries allocated {used} bytes");
            Assert.That(street, Is.Not.Null);
            Assert.That(hall, Is.Not.Null);
            yield break;

            void Run()
            {
                Q.Raycast(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, out _, _options);
                Q.SweepSphere(PartyA, 0UL, Street(0f, 0.7f, -10f), 0.2f, Vector3.forward, 40f, out _, _options);
                Q.OverlapSphere(PartyA, 0UL, Street(0f, 0.5f, 0f), 1.5f, _hits, _options);
                Q.RaycastAll(PartyA, 0UL, Street(0f, 0.5f, -10f), Vector3.forward, 40f, _hits, _options);
            }
        }
    }
}
