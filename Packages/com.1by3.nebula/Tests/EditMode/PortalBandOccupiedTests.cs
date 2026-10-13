using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-432 (<c>docs/portals.md</c> D9, D10, D26): a closing link waits for the band to empty, and the band is tested on bodies by their extent or their
    /// colliders, not only by their roots. Tier B on <see cref="PortalHarness"/>; no physics scene is needed.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalBandOccupiedTests
    {
        private PortalHarness _h;

        [SetUp] public void SetUp() => _h = new PortalHarness(1);
        [TearDown] public void TearDown() => _h.Dispose();

        [Test]
        public void ABodyWhoseExtentIsInTheBandHoldsAClosingLinkEvenThoughItsRootIsFarAway()
        {
            var link = _h.OpenDoor();
            var ship = _h.W.SpawnServerDriven(_h.PawnPrefab, _h.Street, PortalHarness.At(0f, -10f), Quaternion.identity);
            ship.SetExtent(new Bounds(new Vector3(0f, 0f, 9f), new Vector3(1f, 1f, 4f))); // the hull reaches from 7 to 11 m ahead of the root: into the doorway
            _h.Tick();
            Assert.That(_h.Door.Close(PortalHarness.Owner), Is.True);
            _h.Ticks(3);
            Assert.That(link.State, Is.EqualTo(PortalState.Closing));
            Assert.That(link.Wait, Is.EqualTo(PortalWait.BandOccupied), "the root is 10 m back, the hull is in the doorway");

            ship.ClearExtent();
            _h.Ticks(2);
            Assert.That(link.State, Is.EqualTo(PortalState.Closed), "without the extent nothing of it is in the band");
        }

        [Test]
        public void ABodyWhoseColliderIsInTheBandHoldsAClosingLinkToo()
        {
            var link = _h.OpenDoor();
            var crate = _h.W.SpawnServerDriven(_h.PawnPrefab, _h.Street, PortalHarness.At(0f, -2.4f), Quaternion.identity);
            crate.gameObject.AddComponent<BoxCollider>().size = Vector3.one; // its root is 2.4 m back, its front 1.9 m: in the band
            _h.Tick();
            _h.Door.Close(PortalHarness.Owner);
            _h.Ticks(3);
            Assert.That(link.State, Is.EqualTo(PortalState.Closing));
            crate.transform.position = PortalHarness.At(0f, -3.2f);
            _h.Ticks(2);
            Assert.That(link.State, Is.EqualTo(PortalState.Closed));
        }
    }
}
