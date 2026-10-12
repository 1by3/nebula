using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-430 (<c>docs/portals.md</c> D4, D37 to D39): <see cref="Portals.Map(ulong, ulong, PortalPose)"/> and <see cref="Portals.Turn"/> answer
    /// for the link between two scopes, about the faces and in double: from the links a worker holds, from the portal records a client was
    /// sent (a portal with a fixed far side) and from an instance's pose, which every process learns from lease rows. Tier B, on <see cref="PortalHarness"/>.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalMapTests
    {
        private static readonly Quaternion Turn = Quaternion.Euler(0f, 30f, 0f) * Quaternion.Euler(20f, 0f, 0f);
        private PortalHarness _h;

        [SetUp]
        public void SetUp()
        {
            _h = new PortalHarness(1);
            _h.Door.transform.rotation = Turn;
        }

        [TearDown] public void TearDown() => _h.Dispose();

        private static double Distance(Double3 a, Double3 b) =>
            System.Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

        [Test]
        public void MapAndTurnAgreeWithATemplateLinkBothWaysAndWithTheInstancesPose()
        {
            var link = _h.OpenDoor();
            ulong street = 0UL, room = link.FarScope;
            var origin = _h.Door.NearFace.Origin;
            var inStreet = new PortalPose(origin + PhysicsFrames.Rotate(Turn, new Double3(0.5, 1.0, -2.0)), Turn * Quaternion.Euler(0f, 15f, 0f));

            var inRoom = Portals.Map(street, room, inStreet);
            Assert.That(Distance(inRoom.Position, origin + new Double3(0.5, 1.0, -2.0)), Is.LessThan(1e-4), "the door's own offsets, upright in the room");
            Assert.That(Quaternion.Angle(inRoom.Rotation, Quaternion.Euler(0f, 15f, 0f)), Is.LessThan(0.01f));
            var back = Portals.Map(room, street, inRoom);
            Assert.That(Distance(back.Position, inStreet.Position), Is.LessThan(1e-4), "and back");
            Assert.That(Quaternion.Angle(back.Rotation, inStreet.Rotation), Is.LessThan(0.01f));

            Assert.That(Quaternion.Angle(Portals.Turn(room, street), link.Turn), Is.LessThan(0.01f), "what turns the room's axes into the street's");
            Assert.That(Quaternion.Angle(Portals.Turn(street, room), Quaternion.Inverse(link.Turn)), Is.LessThan(0.01f));
            Assert.That(Quaternion.Angle(Portals.Turn(street, room), InstancePoses.Turn(street, room)), Is.LessThan(0.01f), "it replaces InstancePoses.Turn");
            Assert.That(Distance(Portals.Map(street, room, inStreet.Position), inRoom.Position), Is.LessThan(1e-9), "the position alone");
        }

        [Test]
        public void AProcessThatHoldsNoLinkAnswersFromTheInstancesPose()
        {
            var link = _h.OpenDoor();
            ulong room = link.FarScope;
            var origin = _h.Door.NearFace.Origin;
            var inStreet = new PortalPose(origin + PhysicsFrames.Rotate(Turn, new Double3(0.5, 1.0, -2.0)), Turn);
            var withLink = Portals.Map(0UL, room, inStreet);
            PortalHarness.Disable(_h.Door); // a client, or a worker that does not own the portal: no live link, the lease row's pose only
            Assert.That(Portals.All.Count, Is.EqualTo(0));
            var without = Portals.Map(0UL, room, inStreet);
            Assert.That(Distance(without.Position, withLink.Position), Is.LessThan(1e-6));
            Assert.That(Quaternion.Angle(without.Rotation, withLink.Rotation), Is.LessThan(0.01f));
            Assert.That(Quaternion.Angle(Portals.Turn(room, 0UL), link.Turn), Is.LessThan(0.01f));
        }

        [Test]
        public void UnrelatedScopesAndASingleScopeLeaveAPoseAlone()
        {
            _h.OpenDoor();
            var pose = new PortalPose(new Double3(12.5, 3, -7), Quaternion.Euler(10f, 20f, 30f));
            var same = Portals.Map(0UL, 0UL, pose);
            Assert.That(same.Position, Is.EqualTo(pose.Position));
            Assert.That(same.Rotation, Is.EqualTo(pose.Rotation));
            var unrelated = Portals.Map(ScopeKeys.Hash("nowhere"), ScopeKeys.Hash("nothing"), pose);
            Assert.That(unrelated.Position, Is.EqualTo(pose.Position), "nothing relates them");
            Assert.That(Portals.Turn(ScopeKeys.Hash("nowhere"), ScopeKeys.Hash("nothing")), Is.EqualTo(Quaternion.identity));
        }

        private PortalLink OffsetPortal(string name, Vector3 at, Double3 farOrigin, Quaternion farRotation, string scope)
        {
            var portal = _h.NewPortal(name, at);
            portal.SetFixedFarSide(scope, farOrigin, farRotation);
            var link = portal.Open("", new[] { PortalHarness.Client });
            _h.Ticks(3);
            _h.Warm(link);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open), link.ToString());
            return link;
        }

        [Test]
        public void AFixedFarSideMapsThroughItsTwoFacesFromALiveLinkAndFromTheRecordsAClientWasSent()
        {
            var farOrigin = new Double3(100.0, 0.0, 100.0);
            var farRotation = Quaternion.Euler(0f, 90f, 0f);
            _h.W.Instance.PrepareInstance(_h.Template, "other", farOrigin);
            _h.Ticks(2);
            _h.Door.transform.rotation = Quaternion.identity;
            var link = OffsetPortal("bridge", new Vector3(60f, 0f, 20f), farOrigin, farRotation, "vault/other");
            ulong far = link.FarScope;
            var near = link.NearFace;
            var pose = new PortalPose(near.Origin + new Double3(0.5, 1.0, -2.0), Quaternion.Euler(0f, 15f, 0f));

            // About the faces: far.Origin + (far.Rotation x near.Rotation^-1) (p - near.Origin), here a quarter turn about y and a translation.
            var expectedTurn = farRotation * Quaternion.Inverse(near.Rotation);
            var expected = farOrigin + PhysicsFrames.Rotate(expectedTurn, new Double3(0.5, 1.0, -2.0));
            var live = Portals.Map(0UL, far, pose);
            Assert.That(Distance(live.Position, expected), Is.LessThan(1e-4), "the translation of an offset portal and its quarter turn");
            Assert.That(Quaternion.Angle(live.Rotation, expectedTurn * pose.Rotation), Is.LessThan(0.01f));
            Assert.That(Quaternion.Angle(Portals.Turn(0UL, far), expectedTurn), Is.LessThan(0.01f));
            var back = Portals.Map(far, 0UL, live);
            Assert.That(Distance(back.Position, pose.Position), Is.LessThan(1e-4), "and back");

            // A client was sent the portal's record and its link, and holds no live link.
            var record = link.Portal.ToRecord();
            var linkRecord = Portal.ToLinkRecord(link);
            PortalHarness.Disable(link.Portal);
            var update = new PortalsUpdate();
            update.Upserts.Add(record);
            var links = new PortalLinksUpdate();
            links.Upserts.Add(linkRecord);
            Portals.ApplyRecords(update);
            Portals.ApplyLinks(links);
            var known = Portals.Map(0UL, far, pose);
            Assert.That(Distance(known.Position, expected), Is.LessThan(1e-4), "the same answer from the records");
            Assert.That(Quaternion.Angle(known.Rotation, live.Rotation), Is.LessThan(0.01f));
            Assert.That(Quaternion.Angle(Portals.Turn(far, 0UL), Quaternion.Inverse(expectedTurn)), Is.LessThan(0.01f));
        }

        [Test]
        public void WhereSeveralPortalsJoinTheSameTwoScopesTheNearestFaceIsUsed()
        {
            var farOrigin = new Double3(100.0, 0.0, 100.0);
            _h.W.Instance.PrepareInstance(_h.Template, "other", farOrigin);
            _h.Ticks(2);
            _h.Door.transform.rotation = Quaternion.identity;
            // Two ways into the same room, 40 m apart in the street, each mapping its own doorstep to a different spot there.
            var west = OffsetPortal("west", new Vector3(60f, 0f, 20f), new Double3(100.0, 0.0, 98.0), Quaternion.identity, "vault/other");
            var east = OffsetPortal("east", new Vector3(100f, 0f, 20f), new Double3(100.0, 0.0, 102.0), Quaternion.identity, "vault/other");
            ulong far = west.FarScope;
            var nearWest = new PortalPose(west.NearFace.Origin + new Double3(0.5, 1.0, -2.0), Quaternion.identity);
            var nearEast = new PortalPose(east.NearFace.Origin + new Double3(0.5, 1.0, -2.0), Quaternion.identity);
            Assert.That(Distance(Portals.Map(0UL, far, nearWest).Position, new Double3(100.0, 0.0, 98.0) + new Double3(0.5, 1.0, -2.0)), Is.LessThan(1e-4), "beside the west door");
            Assert.That(Distance(Portals.Map(0UL, far, nearEast).Position, new Double3(100.0, 0.0, 102.0) + new Double3(0.5, 1.0, -2.0)), Is.LessThan(1e-4), "beside the east door");
        }
    }
}
