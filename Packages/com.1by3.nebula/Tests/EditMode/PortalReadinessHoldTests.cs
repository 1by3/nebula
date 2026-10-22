using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// <c>docs/portals.md</c> D10 condition 6: the application holds a far container not ready (<see cref="ReadinessHolds"/>), and a link into it stays
    /// preparing with <see cref="PortalWait.FarContentHeld"/> until the hold is released or runs out, on the worker that owns the container and across
    /// workers, through the far worker's answer to the link's preparation. Tier B on <see cref="PortalHarness"/>.
    /// </summary>
    [Category("Conformance")]
    public sealed class PortalReadinessHoldTests
    {
        private PortalHarness _h;

        [SetUp]
        public void SetUp()
        {
            _h = new PortalHarness(2);
            ReadinessHolds.ClockForTests = () => _h.Now;
        }

        [TearDown] public void TearDown() => _h.Dispose();

        [Test]
        public void AHeldFarContainerKeepsTheLinkPreparingUntilItIsReleased()
        {
            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _h.Tick();
            Assert.That(_h.Hall, Is.Not.Null, "the far container is there once the scope is active");
            ReadinessHolds.Hold(_h.Hall, "populating");
            _h.Ticks(3);
            Assert.That(link.State, Is.EqualTo(PortalState.Preparing));
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarContentHeld), "held, and it says so");

            Assert.That(ReadinessHolds.Release(_h.Hall, "populating"), Is.True);
            _h.Tick();
            Assert.That(link.Wait, Is.EqualTo(PortalWait.ClientWarming), "released: it goes on to the members");
            _h.Warm(link);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
        }

        [Test]
        public void EveryReasonHoldsOnItsOwnAndAnOpenLinkIsNotHeldBack()
        {
            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _h.Tick();
            ReadinessHolds.Hold(_h.Hall, "a");
            ReadinessHolds.Hold(_h.Hall, "b");
            ReadinessHolds.Release(_h.Hall, "a");
            _h.Ticks(2);
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarContentHeld), "b still holds it");
            ReadinessHolds.Release(_h.Hall, "b");
            _h.Tick();
            _h.Warm(link);
            _h.Tick();
            Assert.That(link.State, Is.EqualTo(PortalState.Open));

            ReadinessHolds.Hold(_h.Hall, "late");
            _h.Ticks(2);
            Assert.That(link.State, Is.EqualTo(PortalState.Open), "a hold placed after the link opened leaves it open");
        }

        [Test]
        public void AHoldNobodyReleasesRunsOutSoItNeverWedgesALink()
        {
            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _h.Tick();
            ReadinessHolds.Hold(_h.Hall, "forgotten", 10f);
            _h.Now += 9f;
            _h.Ticks(2);
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarContentHeld));

            LogAssert.Expect(LogType.Warning, new Regex("readiness hold 'forgotten'.*not released within 10 s"));
            _h.Now += 1.5f;
            _h.Tick();
            Assert.That(link.Wait, Is.EqualTo(PortalWait.ClientWarming), "ran out: no longer held");
            Assert.That(ReadinessHolds.IsHeld(_h.Hall), Is.False);
        }

        [Test]
        public void AFarWorkerAnswersHeldThenAnswersForGoodOnRelease()
        {
            _h.HallOnSecondWorker = true;
            var link = _h.Door.Open(PortalHarness.Owner, new[] { PortalHarness.Client });
            _h.Tick();
            Assert.That(_h.Hall, Is.Not.Null);
            ReadinessHolds.Hold(_h.Hall, "populating"); // on the far worker, which owns it
            _h.TickAll(3);
            Assert.That(link.State, Is.EqualTo(PortalState.Preparing));
            Assert.That(link.Wait, Is.EqualTo(PortalWait.FarContentHeld), "the far worker's interim answer says held");
            var answers = _h.Mesh.DeliveredOf(MsgId.InstanceReady, _h.W.Id);
            Assert.That(answers.Count, Is.EqualTo(1), "one interim answer, nothing more while held");
            var interim = answers[0].Read(InstancePreparationMsg.Read);
            Assert.That(interim.Held, Is.True);
            Assert.That(interim.LinkId, Is.EqualTo(link.Id));

            ReadinessHolds.Release(_h.Hall, "populating");
            _h.TickAll(2);
            answers = _h.Mesh.DeliveredOf(MsgId.InstanceReady, _h.W.Id);
            Assert.That(answers.Count, Is.EqualTo(2), "and the final answer on release");
            Assert.That(answers[1].Read(InstancePreparationMsg.Read).Held, Is.False);
            Assert.That(link.Wait, Is.EqualTo(PortalWait.ClientWarming));
            _h.Warm(link);
            _h.TickAll();
            Assert.That(link.State, Is.EqualTo(PortalState.Open));
        }

        [Test]
        public void TheHeldFlagIsATrailingFieldAnOlderBodyLeavesOut()
        {
            var w = new NetworkWriter(64);
            new InstancePreparationMsg { RequestId = 3, LinkId = 9, Success = true, Held = true }.Write(w, MsgId.InstanceReady);
            var bytes = w.ToSegment();
            var full = new NetworkReader(new System.ArraySegment<byte>(bytes.Array, bytes.Offset + 1, bytes.Count - 1));
            var read = InstancePreparationMsg.Read(full);
            Assert.That(read.Held, Is.True);
            Assert.That(read.LinkId, Is.EqualTo(9UL));
            var older = new NetworkReader(new System.ArraySegment<byte>(bytes.Array, bytes.Offset + 1, bytes.Count - 2)); // a body written before Held
            Assert.That(InstancePreparationMsg.Read(older).Held, Is.False);
        }
    }
}
