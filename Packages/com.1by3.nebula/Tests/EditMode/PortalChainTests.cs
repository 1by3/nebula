using NUnit.Framework;
using UnityEngine;
using Nebula.World;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-438: a chain of scopes, a room behind a door and a vault behind a door in that room, as a client holds it once the room in the middle
    /// has left it. The vault keeps the world's frame (<see cref="ScopeFrames.Share"/> shares the frame's owner), and its pose is against the
    /// world (<c>Portal.TemplateFarPose</c>), so relating it to the street or to the room through instance poses alone, with no link at hand,
    /// answers what the two links answer. Tier B, on <see cref="PortalHarness"/>.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalChainTests
    {
        private static readonly Quaternion DoorTurn = Quaternion.Euler(0f, 30f, 0f) * Quaternion.Euler(20f, 0f, 0f);
        private static readonly Quaternion InnerTurn = Quaternion.Euler(0f, 40f, 0f);

        private static double Distance(Double3 a, Double3 b) =>
            System.Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

        [Test]
        public void AnInstanceEnteredFromAnInstanceKeepsTheWorldsFrameWhenTheOneBetweenGoes()
        {
            ulong world = ScopeKeys.Hash("chain-world"), room = ScopeKeys.Hash("chain-room"), vault = ScopeKeys.Hash("chain-vault");
            ScopeFrames.Pin("chain-world", world, 5000.0, 0.0, 0.0);
            try
            {
                ScopeFrames.Share(room, world);
                ScopeFrames.Share(vault, room); // entered from the room while the pawn stood in it
                Assert.That(ScopeFrames.Resolve(vault), Is.EqualTo(world));
                ScopeFrames.Unpin(room); // the room's last container left this process
                Assert.That(ScopeFrames.Resolve(vault), Is.EqualTo(world), "the vault still lives in the world's frame");
                Assert.That(ScopeFrames.Of(vault), Is.SameAs(ScopeFrames.Of(world)));
            }
            finally
            {
                ScopeFrames.Unpin(vault);
                ScopeFrames.Unpin(room);
                ScopeFrames.Unpin(world);
            }
        }

        [Test]
        public void AVaultBehindADoorInARoomIsPosedAgainstTheWorldAndMapsAsItsTwoLinksDo()
        {
            var h = new PortalHarness(1);
            try
            {
                h.Door.transform.rotation = DoorTurn;
                var outer = h.OpenDoor();
                ulong street = 0UL, room = outer.FarScope;
                var annex = h.MakeTemplate("annex", ("room", new Vector3(0f, 2f, 3f), new Vector3(6f, 4f, 6f)));
                var inner = h.NewPortal("inner-door", new Vector3(20f, 0f, 23f), h.Hall.transform, annex);
                inner.transform.localRotation = InnerTurn;
                var innerLink = inner.Open("crew-2", new[] { PortalHarness.Client });
                h.Ticks(4);
                h.Warm(innerLink);
                h.Tick();
                Assert.That(innerLink.State, Is.EqualTo(PortalState.Open), innerLink.ToString());
                ulong vault = innerLink.FarScope;

                // What the two links say: a point of the vault in the room, then in the street; and a point of the room in the vault.
                var inVault = innerLink.FarFace.Origin + new Double3(0.7, 1.2, 2.5);
                var throughLinks = outer.ToNear(innerLink.ToNear(inVault));
                var inRoom = inner.NearFace.Origin + new Double3(-1.5, 1.0, -2.0);
                var roomToVault = innerLink.ToFar(inRoom);
                Assert.That(InstancePoses.TryGet(vault, out _, out var vaultTurn), Is.True, "the vault is turned against the world");
                Assert.That(Quaternion.Angle(vaultTurn, h.Door.NearFace.Rotation * inner.NearFace.Rotation), Is.LessThan(0.01f), "the door's turn and the inner door's, composed");

                // A process that holds no link (a client past the room): the instance poses alone.
                PortalHarness.Disable(inner);
                PortalHarness.Disable(h.Door);
                Assert.That(Portals.All, Does.Not.Contain(inner).And.Not.Contain(h.Door), "neither door is registered: no link at hand");
                // To the millimetre: the vault's origin is rounded to one, as every face's is (Portal.OriginResolution).
                Assert.That(Distance(Portals.Map(vault, street, inVault), throughLinks), Is.LessThan(2e-3), "the vault to the street, through its own pose");
                Assert.That(Distance(Portals.Map(room, vault, inRoom), roomToVault), Is.LessThan(2e-3), "the room to the vault: two instances of one world");
                Assert.That(Distance(Portals.Map(vault, room, Portals.Map(room, vault, inRoom)), inRoom), Is.LessThan(1e-6), "and back, exactly");
            }
            finally { h.Dispose(); }
        }
    }
}
