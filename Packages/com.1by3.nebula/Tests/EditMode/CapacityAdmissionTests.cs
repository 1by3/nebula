using System;
using System.Collections.Generic;
using NUnit.Framework;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula.Tests
{
    /// <summary>
    /// The pure half of explicit capacity limits (docs/capacity-admission.md): deriving the capacity signal from a
    /// cost row, aggregating it over a scope's parts, publishing it on the lease rows, and what the admission hook
    /// is asked and answers. No Unity in here, so the standalone services run the same tests.
    /// </summary>
    public class CapacityAdmissionTests
    {
        private const float TickMs = 1000f / 60f;
        private const double Link = NebulaConfig.DefaultCostLinkBytesPerSec;

        [TearDown]
        public void Reset() => NebulaAdmission.Reset();

        private static ContainerCost Row(string id, string scope, float tickShareMs, long bytesOut = 0, long gatewayBytes = 0)
        {
            var row = new ContainerCost { ContainerId = id, ScopeKey = scope, WorkerId = "w1", TickShareMs = tickShareMs, BytesOutPerSec = bytesOut, GatewayBytesPerSec = gatewayBytes };
            ContainerCost.Resolve(ref row, TickMs, Link);
            return row;
        }

        // ------------------------------------------------------------------------------------ the derivation

        [Test]
        public void SaturationIsTheDominantComponentsShareOfItsOwnBudget()
        {
            var busy = NebulaCapacity.Derive(Row("station", "", tickShareMs: 15f), threshold: 0.9f);
            Assert.IsTrue(busy.Known);
            Assert.AreEqual(CostComponent.Simulation, busy.Dominant);
            Assert.AreEqual(15f / TickMs, busy.Saturation, 1e-3f, "15 ms of a 16.7 ms tick");
            Assert.IsTrue(busy.AtCapacity, "0.9 of the tick budget is at capacity at the default threshold");

            // The same container, expensive in bytes rather than in simulation: the reported component follows.
            var chatty = NebulaCapacity.Derive(Row("plaza", "", tickShareMs: 1f, bytesOut: (long)(Link * 0.95)), threshold: 0.9f);
            Assert.AreEqual(CostComponent.Replication, chatty.Dominant);
            Assert.IsTrue(chatty.AtCapacity);
            Assert.AreEqual(0.95f, chatty.Saturation, 1e-2f);
        }

        [Test]
        public void ACalmContainerIsNotAtCapacityAndAThresholdOfZeroTurnsTheSignalOff()
        {
            Assert.IsFalse(NebulaCapacity.Derive(Row("calm", "", tickShareMs: 2f), 0.9f).AtCapacity);
            var full = Row("station", "", tickShareMs: 16.5f);
            Assert.IsTrue(NebulaCapacity.Derive(full, 0.9f).AtCapacity);
            Assert.IsFalse(NebulaCapacity.Derive(full, 0f).AtCapacity, "CapacitySaturation = 0 turns the whole signal off");
            Assert.IsTrue(NebulaCapacity.Derive(full, 0f).Known, "and still reports the measurement");
        }

        [Test]
        public void AContainerNoWorkerHasReportedIsUnknownAndNeverAtCapacity()
        {
            var nothing = NebulaCapacity.Derive(default, 0.9f);
            Assert.IsFalse(nothing.Known);
            Assert.IsFalse(nothing.AtCapacity, "an unknown target is admitted: a mesh with no cost telemetry behaves as it always did");
        }

        // ------------------------------------------------------------------------------------ the control plane

        private static LocalControlPlane Mesh(out string partA, out string partB, string key = "station/alpha")
        {
            var plane = new LocalControlPlane();
            plane.Connect();
            plane.ActivateScope(new ScopeActivationRequest
            {
                ScopeKey = key,
                Requester = "matchmaker",
                Definition = new ScopeDefinition
                {
                    Kind = ScopeKind.Parts,
                    Parts =
                    {
                        new ScopePart { PartId = "dock", Center = new Vector3(500, 0, 0), Size = new Vector3(40, 40, 40) },
                        new ScopePart { PartId = "hold", Center = new Vector3(560, 0, 0), Size = new Vector3(40, 40, 40) },
                    },
                },
            });
            var scope = plane.FindScope(key);
            partA = scope.ContainerIds[0];
            partB = scope.ContainerIds[1];
            return plane;
        }

        [Test]
        public void TheReadingTravelsOnTheLeaseRowWithoutTouchingTheIdleClock()
        {
            var plane = new LocalControlPlane();
            plane.Connect();
            plane.EnsureContainer("c0");
            var lease = plane.FindLease("c0");
            var stamped = lease.UpdatedAt;

            plane.SetContainerCapacity("c0", 0.94f, CostComponent.Gateway, true);
            Assert.IsTrue(lease.HasCapacity);
            Assert.AreEqual(0.94f, lease.Saturation, 1e-4f);
            Assert.AreEqual(CostComponent.Gateway, lease.Dominant);
            Assert.IsTrue(lease.AtCapacity);
            Assert.AreEqual(stamped, lease.UpdatedAt,
                "the capacity write must not restamp the lease: that is the idle clock the scope lifecycle retires on");

            var info = NebulaCapacity.Of(plane, "c0");
            Assert.IsTrue(info.Known && info.AtCapacity);
            Assert.AreEqual(CostComponent.Gateway, info.Dominant);

            long version = plane.Version;
            plane.SetContainerCapacity("c0", 0.94f, CostComponent.Gateway, true);
            Assert.AreEqual(version, plane.Version, "writing the same reading twice is not a change");
        }

        [Test]
        public void TheReadingSurvivesTheControlPlaneDocument()
        {
            var plane = new LocalControlPlane();
            plane.Connect();
            plane.EnsureContainer("c0");
            plane.EnsureContainer("c1");
            plane.SetContainerCapacity("c0", 0.91f, CostComponent.Replication, true);

            var mirror = new LocalControlPlane();
            mirror.Import(ControlPlaneJson.Parse(plane.ToJson()));

            var c0 = mirror.FindLease("c0");
            Assert.IsTrue(c0.HasCapacity && c0.AtCapacity);
            Assert.AreEqual(CostComponent.Replication, c0.Dominant);
            Assert.AreEqual(0.91f, c0.Saturation, 1e-3f);
            Assert.IsFalse(mirror.FindLease("c1").HasCapacity, "a row nobody has reported carries nothing");
        }

        [Test]
        public void AScopeIsAsFullAsItsWorstPart()
        {
            var plane = Mesh(out string dock, out string hold);
            Assert.IsFalse(NebulaCapacity.OfScope(plane, "station/alpha").Known, "nothing reported yet");

            plane.SetContainerCapacity(dock, 0.2f, CostComponent.Simulation, false);
            var half = NebulaCapacity.OfScope(plane, "station/alpha");
            Assert.IsTrue(half.Known, "one known part is enough to have an answer");
            Assert.IsFalse(half.AtCapacity);

            plane.SetContainerCapacity(hold, 0.97f, CostComponent.Gateway, true);
            var worst = NebulaCapacity.OfScope(plane, "station/alpha");
            Assert.IsTrue(worst.AtCapacity, "one saturated part is a saturated scope: it cannot be split past its authored boundaries");
            Assert.AreEqual(CostComponent.Gateway, worst.Dominant);
            Assert.AreEqual(0.97f, worst.Saturation, 1e-3f);
            Assert.AreEqual("station/alpha", worst.ScopeKey);
        }

        [Test]
        public void ThePublicWorldHasNoScopeCapacityAndItsContainersAreJudgedOneAtATime()
        {
            var plane = new LocalControlPlane();
            plane.Connect();
            plane.EnsureContainer("c0");
            plane.SetContainerCapacity("c0", 0.99f, CostComponent.Simulation, true);

            Assert.IsFalse(NebulaCapacity.OfScope(plane, "").Known);
            Assert.IsTrue(NebulaCapacity.Target(plane, "", "c0").AtCapacity);
            Assert.IsFalse(NebulaCapacity.Target(plane, "", "c1").Known, "a container with no row at all is unknown, not full");
        }

        [Test]
        public void AnUnknownPartNeverMakesABusyScopeLookIdle()
        {
            var plane = Mesh(out string dock, out string hold);
            plane.SetContainerCapacity(dock, 0.95f, CostComponent.Simulation, true);
            // 'hold' never reported.
            Assert.IsTrue(NebulaCapacity.OfScope(plane, "station/alpha").AtCapacity);
            Assert.AreEqual(dock, NebulaCapacity.OfScope(plane, "station/alpha").ContainerId);
        }

        // ------------------------------------------------------------------------------------ the hook

        private static AdmissionRequest Arriving(bool atCapacity, float saturation = 0.95f) => new AdmissionRequest
        {
            Kind = AdmissionKind.Join,
            ScopeKey = "station/alpha",
            Identity = "oidc:staff-1",
            Capacity = new CapacityInfo { Known = true, AtCapacity = atCapacity, Saturation = saturation, Dominant = CostComponent.Simulation },
        };

        [Test]
        public void TheDefaultPolicyRefusesOnlyWhenTheTargetIsAtCapacity()
        {
            Assert.AreEqual(AdmissionAction.Reject, NebulaAdmission.Ask(Arriving(atCapacity: true)).Action);
            Assert.AreEqual(AdmissionAction.Admit, NebulaAdmission.Ask(Arriving(atCapacity: false)).Action);
        }

        [Test]
        public void ThePolicyIsNotAskedAboutATargetBelowCapacityUnlessAlwaysConsultIsSet()
        {
            int asked = 0;
            NebulaAdmission.Decide = (in AdmissionRequest r) => { asked++; return AdmissionDecision.Admit(); };

            NebulaAdmission.Ask(Arriving(atCapacity: false));
            Assert.AreEqual(0, asked, "a join into a target with room does not pay for the hook");

            NebulaAdmission.Ask(Arriving(atCapacity: true));
            Assert.AreEqual(1, asked);

            NebulaAdmission.AlwaysConsult = true;
            NebulaAdmission.Ask(Arriving(atCapacity: false));
            Assert.AreEqual(2, asked, "with AlwaysConsult the game sees every arrival");
        }

        [Test]
        public void TheHookCanLetOneParticularArrivalIntoAFullTarget()
        {
            NebulaAdmission.Decide = (in AdmissionRequest r) =>
                r.Identity.StartsWith("oidc:staff") ? AdmissionDecision.Admit() : AdmissionDecision.Reject("the station is full");

            Assert.AreEqual(AdmissionAction.Admit, NebulaAdmission.Ask(Arriving(atCapacity: true)).Action);

            var stranger = Arriving(atCapacity: true);
            stranger.Identity = "oidc:someone-else";
            var refused = NebulaAdmission.Ask(stranger);
            Assert.AreEqual(AdmissionAction.Reject, refused.Action);
            Assert.AreEqual("the station is full", refused.Reason);
        }

        [Test]
        public void APolicyThatThrowsIsCountedAndReadsAsARefusal()
        {
            NebulaAdmission.Decide = (in AdmissionRequest r) => throw new InvalidOperationException("no");
            int before = NebulaAdmission.PolicyErrors;
#if !NEBULA_SERVICE
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("admission policy threw"));
#endif
            Assert.AreEqual(AdmissionAction.Reject, NebulaAdmission.Ask(Arriving(atCapacity: true)).Action,
                "the failure mode of a wrong answer here is a collapsed tick for everyone already inside");
            Assert.AreEqual(before + 1, NebulaAdmission.PolicyErrors);
        }

        [Test]
        public void NebulaHasASentenceOfItsOwnWhenThePolicyGivesNone()
        {
            var capacity = new CapacityInfo { Known = true, AtCapacity = true, Saturation = 0.93f, Dominant = CostComponent.Replication };
            StringAssert.Contains("replication", NebulaAdmission.DefaultReason(capacity));
            StringAssert.Contains("93", NebulaAdmission.DefaultReason(capacity));
        }

        // ------------------------------------------------------------------------------------ the wire

        [Test]
        public void TheTypedRefusalRoundTripsAndAnOlderMessageStillReads()
        {
            var w = new NetworkWriter(64);
            new JoinRejectedMsg { Reason = "the station is full", Retry = false, Code = JoinRejectReason.AtCapacity, Saturation = 0.94f }.Write(w);
            var seg = w.ToSegment();
            var r = new NetworkReader(seg);
            Assert.AreEqual((byte)MsgId.JoinRejected, r.ReadByte());
            var read = JoinRejectedMsg.Read(r);
            Assert.AreEqual("the station is full", read.Reason);
            Assert.AreEqual(JoinRejectReason.AtCapacity, read.Code);
            Assert.AreEqual(0.94f, read.Saturation, 1e-2f);

            // What a gateway from before this item sends: the string and the retry flag, and nothing after them.
            var old = new NetworkWriter(64);
            old.WriteString("bad token");
            old.WriteByte(0);
            var oldRead = JoinRejectedMsg.Read(new NetworkReader(old.ToSegment()));
            Assert.AreEqual("bad token", oldRead.Reason);
            Assert.AreEqual(JoinRejectReason.None, oldRead.Code, "nothing is assumed about a message that ends early");
            Assert.AreEqual(0f, oldRead.Saturation);
        }

        [Test]
        public void TheHoldReasonIsPartOfTheJoinStatusItAlwaysWas()
        {
            var w = new NetworkWriter(16);
            new JoinStatusMsg { State = JoinState.Starting, EstimatedSeconds = 12, Reason = JoinHoldReason.AtCapacity }.Write(w);
            var r = new NetworkReader(w.ToSegment());
            Assert.AreEqual((byte)MsgId.JoinStatus, r.ReadByte());
            Assert.AreEqual(JoinHoldReason.AtCapacity, JoinStatusMsg.Read(r).Reason);
        }

        // ------------------------------------------------------------------------------------ the JSON

        [Test]
        public void TheCostRowSaysWhetherItIsAtCapacity()
        {
            var telemetry = new MeshTelemetry(() => 0.0) { TickPeriodMs = TickMs, LinkBytesPerSec = Link, CapacitySaturation = 0.9f };
            Assert.IsNull(telemetry.Accept("{\"worker\":\"w1\",\"index\":1,\"tick\":10,\"detail\":false,\"containers\":[" +
                "{\"id\":\"station\",\"players\":40,\"bots\":0,\"serverDriven\":0,\"other\":0,\"ghosts\":0,\"scope\":\"station/alpha\",\"cost\":40,\"tickMs\":16,\"bytesOut\":0,\"gatewayBytes\":0}," +
                "{\"id\":\"plaza\",\"players\":2,\"bots\":0,\"serverDriven\":0,\"other\":0,\"ghosts\":0,\"scope\":\"\",\"cost\":2,\"tickMs\":1,\"bytesOut\":0,\"gatewayBytes\":0}]}", out _));

            string json = telemetry.BuildCostJson();
            StringAssert.Contains("\"capacitySaturation\":0.9", json);
            int station = json.IndexOf("\"station\"", StringComparison.Ordinal);
            int plaza = json.IndexOf("\"plaza\"", StringComparison.Ordinal);
            Assert.Greater(station, 0);
            Assert.Greater(plaza, station, "heaviest first");
            StringAssert.Contains("\"atCapacity\":true", json.Substring(station, plaza - station));
            StringAssert.Contains("\"atCapacity\":false", json.Substring(plaza));

            var rows = new Dictionary<string, ContainerCost>();
            telemetry.CopyContainerCost(rows);
            var capacity = new Dictionary<string, CapacityInfo>();
            NebulaCapacity.Derive(rows, 0.9f, capacity);
            Assert.IsTrue(capacity["station"].AtCapacity);
            Assert.IsFalse(capacity["plaza"].AtCapacity);
        }
    }
}
