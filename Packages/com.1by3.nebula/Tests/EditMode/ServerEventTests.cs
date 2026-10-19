using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-474: tick-stamped server events on a predicted entity. On <see cref="PredictedPairRig"/>: the worker's walker applies a
    /// <see cref="ShoveEvent"/> inside <c>Simulate</c> on its tick, the owner's reports carry it, and the owner's replay applies it on the
    /// same tick, so it ends where the worker did.
    /// </summary>
    public sealed class ServerEventTests
    {
        private PredictedPairRig _rig;

        [TearDown]
        public void TearDown()
        {
            _rig?.Dispose();
            _rig = null;
        }

        private static ShoveEvent Shove(float x) => new ShoveEvent { Velocity = new Vector3(x, 0f, 0f) };

        [Test]
        public void AnEventPostedForTickTChangesTheWorkersTickT()
        {
            _rig = new PredictedPairRig(8);
            _rig.DeliverReports = false;
            _rig.Advance(20);
            var worker = _rig.Worker;
            var before = worker.Identity.LocalPosition;
            Assert.IsTrue(worker.PostServerEvent(Shove(6f)));
            _rig.Advance(); // the worker simulates the next tick
            uint t = _rig.Step - 8u;
            Assert.AreEqual(1, worker.ShovesSeen.Count, "Simulate was shown it once");
            Assert.AreEqual((t, false), worker.ShovesSeen[0], "on the tick after the one it was posted in");
            Assert.That(worker.Identity.LocalPosition.x - before.x, Is.EqualTo(6f * NetworkTime.TickInterval).Within(1e-5f), "and it moved that tick");
            _rig.Advance(5);
            Assert.AreEqual(1, worker.ShovesSeen.Count, "never shown again");
            Assert.AreEqual(1, worker.ServerEventsPosted);
        }

        [Test]
        public void TheOwnersReplayFromBeforeTheEventEndsWhereTheWorkerDoes()
        {
            const uint t = 31;
            _rig = new PredictedPairRig(10);
            _rig.Client.Hands = new Vector3(1f, 0f, 1f);
            _rig.DeliverReports = false;
            // The event is posted in the worker's tick 30 and applies on 31. Nothing has been reported to the owner yet.
            _rig.AfterWorkerTick = s => { if (s == t - 1) _rig.Worker.PostServerEvent(Shove(6f)); };
            _rig.Advance(50);
            var worker = _rig.Worker;
            var client = _rig.Client;
            Assert.AreEqual(1, worker.ShovesSeen.Count);
            Assert.AreEqual(t, worker.ShovesSeen[0].Tick);
            Assert.IsTrue(_rig.EventReports.ContainsKey(t - 1), "the report for tick 30 carried it");
            Assert.AreEqual(0, client.ShovesSeen.Count, "the owner predicted without it so far");

            // The report of tick t - 5 reaches the owner with the event: its state is right so far, and the owner replays from it.
            uint from = t - 5;
            client.ReceiveServerEvents(_rig.EventReports[t - 1]);
            client.ClientReconcile(from, new NetworkReader(_rig.States[from]));
            Assert.AreEqual(1, client.ServerEventReplays, "the event made it replay although the prediction was right so far");
            Assert.AreEqual(0, client.Corrections, "which is not a position error");
            Assert.AreEqual(1, client.ShovesSeen.Count);
            Assert.AreEqual((t, true), client.ShovesSeen[0], "applied on the same tick, in the replay");

            // The replay ended in the worker's state: Simulate for tick 41 started from the pose the worker had after tick 40, bit for bit.
            uint worked = _rig.Step - 10u;
            Assert.AreEqual(40u, worked);
            var replayed = client.Observed[client.Observed.Count - (int)(_rig.Step - worked)];
            Assert.AreEqual(worker.Identity.LocalPosition.x, replayed.x, "x");
            Assert.AreEqual(worker.Identity.LocalPosition.y, replayed.y, "y");
            Assert.AreEqual(worker.Identity.LocalPosition.z, replayed.z, "z");
            Assert.AreEqual(worker.Drift, client.Drift, "and it carries the worker's drift");
            client.ClientReconcile(worked, new NetworkReader(_rig.States[worked]));
            Assert.AreEqual(0, client.Corrections, "the worker's later state agrees with the replay");
            Assert.AreEqual(1, client.ShovesSeen.Count, "and the event was not applied twice");
        }

        [Test]
        public void AnEventForATickTheOwnerHasNotPredictedYetIsSeenByItsOwnSimulation()
        {
            _rig = new PredictedPairRig(10);
            _rig.DeliverReports = false;
            _rig.Advance(15);
            // Posted when the worker's clock says 20 and the entity last ran 5: it applies on 20, which the owner reaches in five ticks.
            NetworkTime.Tick = 20;
            Assert.IsTrue(_rig.Worker.PostServerEvent(Shove(2f)));
            var client = _rig.Client;
            client.ReceiveServerEvents(_rig.Worker.TakeServerEventsForReport());
            _rig.Advance(8);
            Assert.AreEqual(1, client.ShovesSeen.Count);
            Assert.AreEqual((20u, false), client.ShovesSeen[0], "the owner's own tick 20, not a replay");
            Assert.AreEqual(0, client.ServerEventReplays);
            client.ClientReconcile(10, new NetworkReader(_rig.States[10]));
            Assert.AreEqual(0, client.ServerEventReplays, "an event the owner already simulated does not make it replay");
        }

        [Test]
        public void ARepeatedReportDoesNotApplyAnEventAgain()
        {
            _rig = new PredictedPairRig(8);
            _rig.AfterWorkerTick = s => { if (s == 20) _rig.Worker.PostServerEvent(Shove(6f)); };
            _rig.Advance(60);
            // The event travelled in every report for 30 ticks; the owner replayed with it once.
            Assert.AreEqual(1, _rig.Client.ServerEventReplays);
            Assert.AreEqual(1, _rig.Client.ShovesSeen.Count);
            Assert.IsTrue(_rig.Client.ShovesSeen[0].Replaying);
            Assert.AreEqual(0, _rig.Client.Corrections, "and nothing was left to correct");
            Assert.AreEqual(1, _rig.Worker.ShovesSeen.Count);
        }

        [Test]
        public void AnEventAcrossAHandoverStillAppliesOnce()
        {
            _rig = new PredictedPairRig(8);
            _rig.DeliverReports = false;
            _rig.Advance(20);
            uint last = _rig.Step - 8u;
            var first = _rig.Worker;
            Assert.IsTrue(first.PostServerEvent(Shove(6f))); // for last + 1, which first will never simulate
            var blob = new NetworkWriter(256);
            first.WritePendingInputs(blob);

            var second = _rig.Extra("second");
            second.Identity.OwnerClientId = 1;
            second.Identity.HasAuthority = true;
            second.ReadPendingInputs(new NetworkReader(blob.ToSegment()));
            Assert.IsNotNull(second.TakeServerEventsForReport(), "the new worker keeps reporting it to the owner");

            var input = new NetworkWriter(32);
            new WalkInput().Serialize(input);
            for (uint tick = last + 1; tick <= last + 3; tick++)
            {
                second.ServerReceiveInput(tick, new NetworkReader(input.ToArray()));
                NetworkTime.Tick = tick;
                second.NetworkTick(tick, NetworkTime.TickInterval);
            }
            Assert.AreEqual(1, second.ShovesSeen.Count);
            Assert.AreEqual(last + 1, second.ShovesSeen[0].Tick);

            // A tick that was already simulated before the handover is not shown its events again.
            var third = _rig.Extra("third");
            third.Identity.OwnerClientId = 1;
            third.Identity.HasAuthority = true;
            var again = new NetworkWriter(256);
            second.WritePendingInputs(again);
            third.ReadPendingInputs(new NetworkReader(again.ToSegment()));
            for (uint tick = last + 1; tick <= last + 3; tick++)
            {
                third.ServerReceiveInput(tick, new NetworkReader(input.ToArray()));
                third.NetworkTick(tick, NetworkTime.TickInterval);
            }
            Assert.AreEqual(0, third.ShovesSeen.Count, "already applied by the worker before it");
        }

        [Test]
        public void AtMostAFewEventsApplyOnOneTickAndTheRestAreCounted()
        {
            _rig = new PredictedPairRig(8);
            _rig.DeliverReports = false;
            _rig.Advance(10);
            int accepted = 0;
            for (int i = 0; i < 7; i++) if (_rig.Worker.PostServerEvent(Shove(1f))) accepted++;
            Assert.AreEqual(PredictedBehaviourBase.MaxServerEventsPerTick, accepted);
            Assert.AreEqual(7 - PredictedBehaviourBase.MaxServerEventsPerTick, _rig.Worker.ServerEventsDropped);
            _rig.Advance();
            Assert.AreEqual(PredictedBehaviourBase.MaxServerEventsPerTick, _rig.Worker.ShovesSeen.Count, "the ones that fitted all applied, in order");
        }

        [Test]
        public void OnlyTheAuthorityCanPostEvents()
        {
            _rig = new PredictedPairRig(8);
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("only the authoritative worker"));
            Assert.IsFalse(_rig.Client.PostServerEvent(Shove(1f)));
            Assert.IsFalse(_rig.Client.PostServerEvent(Shove(1f)), "and says so once");
            Assert.AreEqual(0, _rig.Client.ServerEventsPosted);
        }

        [Test]
        public void TheEventsSectionIsTrailingAndLeftOutForAClientThatDoesNotReadIt()
        {
            var msg = new OwnerStateMsg
            {
                NetId = 5, Epoch = 2, Tick = 90, LastInputTick = 88, InputLead = 3, OwnerClientId = 7,
                State = new byte[] { 1, 2, 3 },
                Events = new byte[] { 1, 90, 0, 0, 0, 0, 9, 9, 9, 9, 2, 5, 6 },
            };
            var plain = new NetworkWriter(128);
            msg.Write(plain, includeEvents: false);
            var full = new NetworkWriter(128);
            msg.Write(full);
            var section = new NetworkWriter(64);
            section.WriteBytes(msg.Events);
            Assert.AreEqual(plain.Length + section.Length, full.Length, "a length-prefixed section after the state");
            var withoutEvents = msg;
            withoutEvents.Events = null;
            var none = new NetworkWriter(128);
            withoutEvents.Write(none);
            Assert.AreEqual(plain.ToArray(), none.ToArray(), "no events: the bytes of protocol 28");

            var reader = new NetworkReader(full.ToArray());
            reader.ReadByte();
            var read = OwnerStateMsg.Read(reader);
            Assert.AreEqual(msg.Events, read.Events);
            Assert.AreEqual(msg.State, read.State);
            var reader28 = new NetworkReader(plain.ToArray());
            reader28.ReadByte();
            Assert.IsNull(OwnerStateMsg.Read(reader28).Events);
        }
    }
}
