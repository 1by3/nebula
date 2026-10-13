using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-431 (<c>docs/portals.md</c> D3, D20, D21): a portal's windows. Each side of a link has a window, the cross-section grown by the portal's
    /// <c>WindowMargin</c> sideways and extruded <c>WindowDepth</c> into that side, an oriented box in the face's axes, built from the portal's record alone so the
    /// network gateway can decide who may look through without running game code. Pure geometry in double, then what the worker publishes
    /// (a template's far face, the window sizes) and how a game declares observers beyond a link's members. The network gateway's half is
    /// <c>Services~/Nebula.Services.Tests/ConformancePortalWindowTests.cs</c>.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalWindowTests
    {
        private static PortalRecord Record(Quaternion nearRotation, Double3 nearOrigin, Quaternion farRotation, Double3 farOrigin) => new PortalRecord
        {
            PortalId = 1, NearScope = 10, FarScope = 20, NearOrigin = nearOrigin, NearRotation = nearRotation, FarOrigin = farOrigin, FarRotation = farRotation,
            ApertureKind = PortalApertureKind.Box, HalfWidth = 1.5f, HalfHeight = 2.2f, WindowMargin = 8f, WindowDepth = 48f,
        };

        [Test]
        public void TheNearWindowIsTheApertureGrownSidewaysAndExtrudedIntoTheNearSide()
        {
            var window = Record(Quaternion.identity, new Double3(100, 0, 100), Quaternion.identity, Double3.Zero).NearWindow;
            Assert.That(window.Scope, Is.EqualTo(10UL));
            Assert.That(window.HalfWidth, Is.EqualTo(9.5f), "the aperture's half width plus the margin");
            Assert.That(window.HalfHeight, Is.EqualTo(10.2f).Within(1e-5f));
            Assert.That(window.Depth, Is.EqualTo(48f));
            Assert.That(window.Contains(new Double3(100, 0, 99)), Is.True, "just before the face, on the near side (z < 0 in the face's axes)");
            Assert.That(window.Contains(new Double3(100, 0, 52)), Is.True, "48 m in");
            Assert.That(window.Contains(new Double3(100, 0, 51.9)), Is.False, "deeper than the window reaches");
            Assert.That(window.Contains(new Double3(100, 0, 100.1)), Is.False, "beyond the face is the far side's");
            Assert.That(window.Contains(new Double3(109.4, 0, 90)), Is.True, "inside the margin");
            Assert.That(window.Contains(new Double3(109.6, 0, 90)), Is.False, "beyond the margin");
            Assert.That(window.Contains(new Double3(100, 10.3, 90)), Is.False, "above it");
        }

        [Test]
        public void TheFarWindowIsOnTheOtherSideOfItsFaceInThatFacesOwnAxes()
        {
            // The far face on the room's wall, turned a quarter about y: its +z is the world's +x.
            var record = Record(Quaternion.identity, new Double3(10, 0, 10), Quaternion.Euler(0f, 90f, 0f), new Double3(480, 0, 0));
            var window = record.FarWindow();
            Assert.That(window.Scope, Is.EqualTo(20UL), "a fixed far side's own scope");
            Assert.That(record.FarWindow(33).Scope, Is.EqualTo(33UL), "or the link's, for a template");
            Assert.That(window.Contains(new Double3(490, 1, 3)), Is.True, "10 m along the face's +z, 3 m to one side");
            Assert.That(window.Contains(new Double3(527.9, 0, 0)), Is.True);
            Assert.That(window.Contains(new Double3(528.1, 0, 0)), Is.False);
            Assert.That(window.Contains(new Double3(479.9, 0, 0)), Is.False, "behind the face is the near side's");
            Assert.That(window.Contains(new Double3(490, 0, 15)), Is.False, "beside the window: the face's x is the world's -z");
            window.Bounds(out var min, out var max);
            Assert.That(min.X, Is.EqualTo(480.0).Within(1e-4));
            Assert.That(max.X, Is.EqualTo(528.0).Within(1e-4));
            Assert.That(min.Z, Is.EqualTo(-9.5).Within(1e-4));
            Assert.That(max.Z, Is.EqualTo(9.5).Within(1e-4));
            Assert.That(max.Y - min.Y, Is.EqualTo(20.4).Within(1e-4));
        }

        [Test]
        public void AWindowIsExactToTheMillimetreFiveTimesTenToTheSeventhMetresOut()
        {
            var origin = new Double3(5.0e7, 123.456, -5.0e7);
            var turn = Quaternion.Euler(55f, 180f, 20f);
            var window = Record(turn, origin, Quaternion.identity, Double3.Zero).NearWindow;
            // A point 3 m along the face's -z and 4 m up its y, whatever the face's turn.
            var inside = window.ToAbsolute(new Double3(2.0, 4.0, -3.0));
            var outside = window.ToAbsolute(new Double3(9.4, 0.0, -49.0));
            Assert.That(window.Contains(inside), Is.True);
            Assert.That(window.Contains(outside), Is.False, "one meter too deep, and a window is not the float of a position 5e7 out");
            var back = window.ToLocal(inside);
            Assert.That(back.X, Is.EqualTo(2.0).Within(1e-3));
            Assert.That(back.Y, Is.EqualTo(4.0).Within(1e-3));
            Assert.That(back.Z, Is.EqualTo(-3.0).Within(1e-3));
        }

        [Test]
        public void AnEmptyWindowHoldsNothing()
        {
            var record = Record(Quaternion.identity, Double3.Zero, Quaternion.identity, Double3.Zero);
            record.WindowDepth = 0f;
            Assert.That(record.NearWindow.IsEmpty, Is.True);
            Assert.That(record.NearWindow.Contains(new Double3(0, 0, -0.5)), Is.False);
            Assert.That(record.FarWindow().Contains(new Double3(0, 0, 0.5)), Is.False);
        }

        // ------------------------------------------------------------------------------------------ what the worker publishes

        [Test]
        public void ATemplatePortalPublishesItsFarFaceAndWindowSizes()
        {
            using var h = new PortalHarness(1);
            h.Door.WindowMargin = 6f;
            h.Door.WindowDepth = 30f;
            var record = h.Door.ToRecord();
            Assert.That(record.WindowMargin, Is.EqualTo(6f));
            Assert.That(record.WindowDepth, Is.EqualTo(30f));
            Assert.That(record.FarOrigin, Is.EqualTo(record.NearOrigin), "a template's far face is the near face's origin in the instance's own upright space");
            Assert.That(record.FarRotation, Is.EqualTo(Quaternion.identity), "with the identity rotation when the portal turns with its face");
            h.Door.TurnWithFace = false;
            var kept = h.Door.ToRecord();
            Assert.That(kept.FarRotation, Is.EqualTo(kept.NearRotation), "and the near face's own when it does not");
            Assert.That(record.NearWindow.Depth, Is.EqualTo(30f));
            Assert.That(record.NearWindow.HalfWidth, Is.EqualTo(2f + 6f));
        }

        [Test]
        public void AGameLetsABystanderLookThroughALinkWithoutMakingItAMember()
        {
            using var h = new PortalHarness(1);
            var link = h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            Assert.That(link.IsObserver(99), Is.False);
            Assert.That(h.Door.AddObserver(PortalHarness.Owner, 99), Is.True);
            Assert.That(h.Door.AddObserver(PortalHarness.Owner, 99), Is.False, "once");
            Assert.That(link.IsObserver(99), Is.True);
            Assert.That(link.IsMember(99), Is.False, "the link never waits for it and never admits its entities");
            Assert.That(h.Door.AddObserver("nobody", 99), Is.False, "a party without a link has nothing to look through");
            h.Tick();
            var published = new List<PortalLinkRecord>();
            foreach (var m in h.Mesh.DeliveredOf(MsgId.PortalLinks, "gw")) published.AddRange(m.Read(PortalLinksMsg.Read).Upserts);
            Assert.That(published[published.Count - 1].Observers, Is.SupersetOf(new ulong[] { PortalHarness.Client, 99 }), "the network gateway is told");
            Assert.That(published[published.Count - 1].Members, Is.EquivalentTo(new[] { PortalHarness.Client }));
            Assert.That(h.Door.RemoveObserver(PortalHarness.Owner, 99), Is.True);
            Assert.That(link.IsObserver(99), Is.False);
        }
    }
}
