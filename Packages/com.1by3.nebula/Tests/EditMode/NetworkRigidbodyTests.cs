using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// <see cref="NetworkRigidbody"/>'s two reasons to keep the authoritative body kinematic: the game's
    /// <see cref="NetworkRigidbody.Simulate"/> gate and <see cref="NetworkRigidbody.Held"/>, the hold an attached
    /// entity puts on it (<c>docs/frame-bodies.md</c> D9), across spawn, gaining and losing authority, and a handover.
    /// </summary>
    public sealed class NetworkRigidbodyTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            NebulaRuntime.Reset();
            NebulaRuntime.IsServer = true;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
            NebulaRuntime.Reset();
        }

        private NetworkRigidbody Prop(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            var nr = go.AddComponent<NetworkRigidbody>();
            nr.Identity.Initialize();
            return nr;
        }

        private static void Spawn(NetworkRigidbody nr, bool authority)
        {
            nr.Identity.HasAuthority = authority;
            nr.Identity.InvokeSpawn();
        }

        [Test]
        public void HeldBeforeSpawnKeepsTheBodyKinematicUntilReleased()
        {
            var nr = Prop("held-at-spawn");
            nr.Held = true;
            Spawn(nr, authority: true);
            Assert.IsTrue(nr.Body.isKinematic, "held from its first tick");

            nr.transform.position = new Vector3(3f, 2f, 1f);
            nr.Held = false;
            Assert.IsFalse(nr.Body.isKinematic, "released to the solver");
            Assert.AreEqual(new Vector3(3f, 2f, 1f), nr.Body.position, "from the pose the transform has now");
        }

        [Test]
        public void EitherReasonHoldsTheBody()
        {
            var nr = Prop("two-reasons");
            Spawn(nr, authority: true);
            Assert.IsFalse(nr.Body.isKinematic);

            nr.Held = true;
            nr.Simulate = false;
            Assert.IsTrue(nr.Body.isKinematic);
            nr.Held = false;
            Assert.IsTrue(nr.Body.isKinematic, "the Simulate gate still holds it");
            nr.Held = true;
            nr.Simulate = true;
            Assert.IsTrue(nr.Body.isKinematic, "the hold still holds it");
            nr.Held = false;
            Assert.IsFalse(nr.Body.isKinematic, "both reasons gone");
        }

        [Test]
        public void AHeldGhostStaysKinematicAndGainsAuthorityHeld()
        {
            var nr = Prop("held-ghost");
            Spawn(nr, authority: false);
            nr.Held = true;
            nr.Held = false;
            Assert.IsTrue(nr.Body.isKinematic, "a ghost is kinematic whatever the hold says");

            nr.Held = true;
            nr.Identity.SetAuthority(true);
            Assert.IsTrue(nr.Body.isKinematic, "held as it gained authority");
            nr.Held = false;
            Assert.IsFalse(nr.Body.isKinematic);

            nr.Held = true;
            nr.Identity.SetAuthority(false);
            nr.Held = false;
            Assert.IsTrue(nr.Body.isKinematic, "lost authority: kinematic again");
        }

        [Test]
        public void TheHandoverCarriesTheBodysOwnFlagNotTheHold()
        {
            var from = Prop("held-from");
            Spawn(from, authority: true);
            from.Held = true;
            var w = new NetworkWriter();
            from.WriteHandoverState(w);

            // The receiver is told the body underneath is dynamic; whether it is held is the receiver's own state.
            var to = Prop("held-to");
            Spawn(to, authority: false);
            to.ReadHandoverState(new NetworkReader(w.ToSegment()));
            to.Identity.SetAuthority(true);
            Assert.IsFalse(to.Body.isKinematic, "not held on the receiver: dynamic, as the flag underneath says");

            var held = Prop("held-to-held");
            Spawn(held, authority: false);
            held.Held = true;
            held.ReadHandoverState(new NetworkReader(w.ToSegment()));
            held.Identity.SetAuthority(true);
            Assert.IsTrue(held.Body.isKinematic, "held on the receiver");
            held.Held = false;
            Assert.IsFalse(held.Body.isKinematic, "and dynamic underneath once released");
        }

        [Test]
        public void AnAuthoredKinematicBodyStaysKinematicWhenTheHoldClears()
        {
            var nr = Prop("kinematic-prop");
            Spawn(nr, authority: true);
            nr.Body.isKinematic = true; // the game made it kinematic on purpose
            nr.Held = true;
            nr.Held = false;
            Assert.IsTrue(nr.Body.isKinematic, "the flag underneath came back");
        }
    }
}
