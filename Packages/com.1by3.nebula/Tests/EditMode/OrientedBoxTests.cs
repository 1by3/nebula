using System;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The oriented box an <see cref="InstanceBoundary"/> tests crossings in. Pure C#, so the standalone test runner
    /// runs it too. A door 4 m wide, 4 m tall and 1 m deep stands on ground tilted 35 degrees about x.
    /// </summary>
    public sealed class OrientedBoxTests
    {
        // Rotation of 35 degrees about x, and its axes.
        private static readonly float Half = 35f * (float)Math.PI / 180f * 0.5f;
        private static readonly float Qx = (float)Math.Sin(Half), Qw = (float)Math.Cos(Half);
        private static readonly float Cos = (float)Math.Cos(35f * Math.PI / 180f), Sin = (float)Math.Sin(35f * Math.PI / 180f);

        // Box centre (0, 2, 0), half extents (2, 2, 0.5), in the door's own axes.
        private static bool Door(float x, float y, float z, float margin = 0f, bool tilted = true) =>
            OrientedBox.Contains(x, y, z, tilted ? Qx : 0f, 0f, 0f, tilted ? Qw : 1f, 0f, 2f, 0f, 2f, 2f, 0.5f, margin);

        // A point at (x, y, z) in the door's own axes, as an offset in the scope's axes.
        private static (float x, float y, float z) Scope(float x, float y, float z) =>
            (x, y * Cos - z * Sin, y * Sin + z * Cos);

        [Test]
        public void ATiltedBoxDetectsAPointThroughItsDoor()
        {
            var p = Scope(0f, 2f, 0f);
            Assert.IsTrue(Door(p.x, p.y, p.z));
            p = Scope(1.5f, 3.5f, 0.4f);
            Assert.IsTrue(Door(p.x, p.y, p.z));
        }

        [Test]
        public void ATiltedBoxIgnoresPointsBesideItsDoor()
        {
            // Just beyond the doorway's depth, and above its top, both in the box's own axes.
            var p = Scope(0f, 2f, 0.7f);
            Assert.IsFalse(Door(p.x, p.y, p.z));
            p = Scope(0f, 4.2f, 0f);
            Assert.IsFalse(Door(p.x, p.y, p.z));
            p = Scope(2.2f, 2f, 0f);
            Assert.IsFalse(Door(p.x, p.y, p.z));
        }

        [Test]
        public void AnAxisAlignedBoxWouldHaveGotItWrong()
        {
            // 3.9 m up the tilted door's face is inside the doorway; as scope-axis offsets it is outside the unturned box.
            var p = Scope(0f, 3.9f, 0.45f);
            Assert.IsTrue(Door(p.x, p.y, p.z));
            Assert.IsFalse(Door(p.x, p.y, p.z, tilted: false));
            // A point in the ground under the untilted box is not in the tilted doorway.
            Assert.IsTrue(Door(0f, 0.3f, -0.45f, tilted: false));
            Assert.IsFalse(Door(0f, 0.3f, -0.45f));
        }

        [Test]
        public void TheMarginWidensEverySideOfTheTurnedBox()
        {
            var p = Scope(0f, 2f, 0.9f);
            Assert.IsFalse(Door(p.x, p.y, p.z));
            Assert.IsTrue(Door(p.x, p.y, p.z, margin: 0.5f));
        }

        [Test]
        public void TheIdentityRotationIsAnAxisAlignedBoxWithInclusiveEdges()
        {
            Assert.IsTrue(Door(2f, 4f, 0.5f, tilted: false));
            Assert.IsFalse(Door(2.01f, 2f, 0f, tilted: false));
            Assert.IsTrue(Door(2.5f, 2f, 0f, margin: 0.5f, tilted: false));
            Assert.IsFalse(Door(0f, -0.01f, 0f, tilted: false));
        }
    }
}
