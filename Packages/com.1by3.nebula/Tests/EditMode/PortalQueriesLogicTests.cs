using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// The rules of NEB-433 (<c>docs/portals.md</c> D32, D33) that need no physics scene: where a cast meets a face's cross-section, who is asking, the options a
    /// query takes and the flags a hit carries. The physical tests, on real scenes, are in <c>Tests/PlayMode</c>.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalQueriesLogicTests
    {
        private static QFace Face(Quaternion rotation, Vector3 origin, bool entering = true) => new QFace
        {
            Frame = new PortalFaceFrame(origin, rotation),
            Aperture = PortalAperture.Box(2f, 2.5f),
            Entering = entering,
        };

        [Test]
        public void ACastMeetsTheNearFacesOpeningAtTheDistanceItReaches()
        {
            var face = Face(Quaternion.identity, new Vector3(20f, 2.5f, 20f));
            Assert.That(PortalQueries.PlaneHit(face, new Vector3(20f, 1f, 10f), Vector3.forward, 40f, out float t), Is.True);
            Assert.That(t, Is.EqualTo(10f).Within(1e-4f));
            // Not far enough.
            Assert.That(PortalQueries.PlaneHit(face, new Vector3(20f, 1f, 10f), Vector3.forward, 9.9f, out _), Is.False);
            // Beside the opening, above it, and parallel to the plane.
            Assert.That(PortalQueries.PlaneHit(face, new Vector3(23f, 1f, 10f), Vector3.forward, 40f, out _), Is.False);
            Assert.That(PortalQueries.PlaneHit(face, new Vector3(20f, 6f, 10f), Vector3.forward, 40f, out _), Is.False);
            Assert.That(PortalQueries.PlaneHit(face, new Vector3(20f, 1f, 10f), Vector3.right, 40f, out _), Is.False);
            // Going the wrong way: away from the plane, and already past it.
            Assert.That(PortalQueries.PlaneHit(face, new Vector3(20f, 1f, 10f), Vector3.back, 40f, out _), Is.False);
            Assert.That(PortalQueries.PlaneHit(face, new Vector3(20f, 1f, 21f), Vector3.forward, 40f, out _), Is.False);
        }

        [Test]
        public void AnObliqueCastMeetsTheOpeningWhereItsPathDoes()
        {
            var face = Face(Quaternion.identity, new Vector3(0f, 0f, 0f));
            var dir = new Vector3(0.5f, 0f, 1f).normalized;
            // From (-3, 0, -4): at z = 0 it has moved 4 m along z and 2 m along x, so it is at x = -1: inside the opening.
            Assert.That(PortalQueries.PlaneHit(face, new Vector3(-3f, 0f, -4f), dir, 100f, out float t), Is.True);
            Assert.That(t, Is.EqualTo(4f / dir.z).Within(1e-3f));
            // From (-5, 0, -4) it is at x = -3 when it gets there: outside.
            Assert.That(PortalQueries.PlaneHit(face, new Vector3(-5f, 0f, -4f), dir, 100f, out _), Is.False);
        }

        [Test]
        public void ATurnedFaceIsMeasuredInItsOwnAxes()
        {
            var turn = Quaternion.Euler(0f, 90f, 0f);
            var face = Face(turn, new Vector3(0f, 0f, 0f));
            // The face looks along +x: a cast along +x meets it, one along +z does not.
            Assert.That(PortalQueries.PlaneHit(face, new Vector3(-6f, 0f, 1f), Vector3.right, 40f, out float t), Is.True);
            Assert.That(t, Is.EqualTo(6f).Within(1e-4f));
            Assert.That(PortalQueries.PlaneHit(face, new Vector3(-6f, 0f, 1f), Vector3.forward, 40f, out _), Is.False);
            // 3 m to the side of the opening's centre along the face's x axis (the scene's -z): outside its 2 m half width.
            Assert.That(PortalQueries.PlaneHit(face, new Vector3(-6f, 0f, -3f), Vector3.right, 40f, out _), Is.False);
        }

        [Test]
        public void TheFarFacesWayThroughIsItsNegativeZ()
        {
            var face = Face(Quaternion.identity, new Vector3(20f, 2.5f, 20f), entering: false);
            // Inside the room, going out toward the plane.
            Assert.That(PortalQueries.PlaneHit(face, new Vector3(20f, 1f, 26f), Vector3.back, 40f, out float t), Is.True);
            Assert.That(t, Is.EqualTo(6f).Within(1e-4f));
            // Going deeper into the room is not leaving it.
            Assert.That(PortalQueries.PlaneHit(face, new Vector3(20f, 1f, 26f), Vector3.forward, 40f, out _), Is.False);
            // A cast that starts on the plane inside the opening leaves at once.
            Assert.That(PortalQueries.PlaneHit(face, new Vector3(20f, 1f, 20f), Vector3.back, 40f, out float zero), Is.True);
            Assert.That(zero, Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void AnAskerIsAnEntityAPartyOrNobody()
        {
            Assert.That(PortalAsker.None.HasParty, Is.False);
            Assert.That(PortalAsker.None.Entity, Is.Null);
            var party = PortalAsker.OfParty("crew-91");
            Assert.That(party.HasParty, Is.True);
            Assert.That(party.Party, Is.EqualTo("crew-91"));
            Assert.That(PortalAsker.OfParty(null).Party, Is.EqualTo(""), "the empty party is a party: a fixed far side's");
            Assert.That(PortalAsker.Of(null).HasParty, Is.False, "an entity's party is derived at the face");
        }

        [Test]
        public void ThePartyHashIsTheOneTheLinksCarryAndIsKept()
        {
            Assert.That(PortalQueries.HashOf("crew-91"), Is.EqualTo(PortalIds.PartyHash("crew-91")));
            Assert.That(PortalQueries.HashOf(""), Is.EqualTo(PortalIds.PartyHash("")));
            Assert.That(PortalQueries.HashOf("crew-91"), Is.EqualTo(PortalQueries.HashOf(new string("crew-91".ToCharArray()))), "by value, not by reference");
        }

        [Test]
        public void TheOptionsNeverAskForTheProxyLayerAndDefaultToEverythingElse()
        {
            var options = new PortalQueryOptions();
            Assert.That(options.Tick, Is.EqualTo(0u));
            Assert.That(options.RewindNear, Is.False);
            Assert.That(options.Space, Is.Null);
            Assert.That(options.Triggers, Is.EqualTo(QueryTriggerInteraction.Ignore));
            int proxy = 1 << Portals.ProxyLayer;
            Assert.That(options.EffectiveMask & proxy, Is.EqualTo(0), "never the proxies");
            Assert.That(options.EffectiveMask | proxy, Is.EqualTo(-1), "and every other layer");
            options.LayerMask = -1;
            Assert.That(options.EffectiveMask & proxy, Is.EqualTo(0), "even when every layer is asked for");
            options.LayerMask = 1 << 8;
            Assert.That(options.EffectiveMask, Is.EqualTo(1 << 8));
        }

        [Test]
        public void AHitSaysWhatItIsFromItsFlags()
        {
            var plane = new PortalHit(5UL, null, null, 3f, Vector3.one, Vector3.zero, Vector3.up, Vector3.up, PortalHitFlags.Aperture | PortalHitFlags.ThroughPortal, 9UL);
            Assert.That(plane.IsAperture, Is.True);
            Assert.That(plane.IsThroughPortal, Is.True);
            Assert.That(plane.IsTruncated, Is.False);
            Assert.That(plane.LinkId, Is.EqualTo(9UL));
            Assert.That(plane.PointInAskerSpace, Is.EqualTo(Vector3.one));
            Assert.That(plane.PointInScope, Is.EqualTo(Vector3.zero));
            var edge = new PortalHit(5UL, null, null, 12f, Vector3.zero, Vector3.zero, Vector3.up, Vector3.up, PortalHitFlags.Truncated | PortalHitFlags.ThroughPortal, 9UL);
            Assert.That(edge.IsTruncated, Is.True);
            Assert.That(edge.IsAperture, Is.False);
            Assert.That(default(PortalHit).Collider, Is.Null);
        }

        [Test]
        public void ACrossingSaysWhereTheCastWentThroughAndNoCrossingIsNone()
        {
            var crossing = new PortalCrossing(9UL, 1UL, 2UL, 4.5f, new Vector3(1f, 2f, 3f), Vector3.back);
            Assert.That(crossing.Crossed, Is.True);
            Assert.That(crossing.LinkId, Is.EqualTo(9UL));
            Assert.That(crossing.FromScope, Is.EqualTo(1UL));
            Assert.That(crossing.ToScope, Is.EqualTo(2UL));
            Assert.That(crossing.Distance, Is.EqualTo(4.5f));
            Assert.That(crossing.FarPoint, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(crossing.FarDirection, Is.EqualTo(Vector3.back));
            Assert.That(PortalCrossing.None.Crossed, Is.False);
        }
    }
}
