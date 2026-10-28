using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// A predicted entity driven by the physics engine: its <c>Simulate</c> sets the body's velocity and steps its own physics scene, so it
    /// cannot be stepped through skipped ticks (<see cref="PredictedBehaviourBase.StepsSkippedServerTicks"/> stays false, NEB-412).
    /// </summary>
    public class PairRoller : PredictedBehaviour<WalkInput>
    {
        public Vector3 Hands;
        public Rigidbody Body;
        public Scene PhysicsHome;
        protected override WalkInput GatherInput() => new WalkInput { Move = Hands };

        protected override void Simulate(uint tick, in WalkInput input, float deltaTime)
        {
            Body.linearVelocity = input.Move;
            PhysicsHome.GetPhysicsScene().Simulate(deltaTime);
            Identity.Motion.Velocity = Body.linearVelocity;
        }

        protected override void ReadState(NetworkReader reader)
        {
            base.ReadState(reader);
            Body.position = transform.position;
            Body.linearVelocity = Identity.Motion.Velocity;
        }
    }

    /// <summary>
    /// NEB-484: a worker that skips tick numbers for a predicted entity it does not step through them tells the owner which ticks it dropped,
    /// and the owner re-bases on the next report: the worker's state is its new base, the inputs of the dropped ticks are not part of it, and
    /// no misprediction is counted. What moves is only what the dropped inputs moved, the real divergence.
    /// </summary>
    public sealed class DroppedTickRebaseTests
    {
        [TearDown]
        public void TearDown()
        {
            NetworkTime.Tick = 0;
            PredictedBehaviourBase.ReplaySkippedTicks = true;
        }

        private static readonly Vector3 Hands = new Vector3(6f, 0f, 3f);
        private static float DroppedMotion(int ticks) => (Hands * NetworkTime.TickInterval * ticks).magnitude;

        [Test]
        public void TheDroppedRangeTravelsToAClientOf31AndIsLeftOutForOneOf30()
        {
            var msg = new OwnerStateMsg { NetId = 7, Epoch = 1, Tick = 120, LastInputTick = 119, State = new byte[] { 1, 2, 3 }, DroppedFrom = 100, DroppedTo = 109 };
            var w = new NetworkWriter();
            msg.Write(w, includeEvents: true, includeDropped: true);
            var r = new NetworkReader(w.ToSegment());
            r.ReadByte();
            var read = OwnerStateMsg.Read(r);
            Assert.AreEqual(100u, read.DroppedFrom);
            Assert.AreEqual(109u, read.DroppedTo);
            Assert.IsNull(read.Events, "an empty events section in front of the range reads as none");

            var old = new NetworkWriter();
            msg.Write(old, includeEvents: true, includeDropped: false);
            var plain = new NetworkWriter();
            new OwnerStateMsg { NetId = 7, Epoch = 1, Tick = 120, LastInputTick = 119, State = new byte[] { 1, 2, 3 } }.Write(plain, true);
            Assert.AreEqual(plain.ToArray(), old.ToArray(), "a client of 30 is sent the bytes it always was");
            r = new NetworkReader(old.ToSegment());
            r.ReadByte();
            Assert.AreEqual(0u, OwnerStateMsg.Read(r).DroppedTo);
        }

        [Test]
        public void ASkipTheEntityIsNotSteppedThroughIsARebaseNotACorrection()
        {
            using var rig = new PredictedPairRig(lag: 3);
            rig.Client.Hands = Hands;
            rig.Advance(20);
            int corrections = rig.Client.Corrections;
            rig.SkipWorkerTicks(10);
            rig.Advance(30);
            Assert.AreEqual(corrections, rig.Client.Corrections, "no misprediction is counted");
            Assert.AreEqual(1, rig.Client.Rebases, "the first report after the dropped ticks re-based");
            Assert.AreEqual(10, rig.Worker.TicksDropped);
            Assert.That(rig.Client.MaxRebaseMagnitude, Is.LessThanOrEqualTo(DroppedMotion(10) + 1e-3f), "no more than what the dropped inputs moved");
            Assert.That(rig.Client.MaxRebaseMagnitude, Is.GreaterThan(DroppedMotion(10) * 0.9f), "and that much: the worker never ran them");
            Assert.That(Vector3.Distance(rig.Client.SimulatedPosition, rig.Worker.Identity.LocalPosition + Hands * NetworkTime.TickInterval * rig.Lag), Is.LessThan(1e-3f),
                "the owner predicts on from the worker's state");
        }

        [Test]
        public void WithoutTheReportTheSameSkipIsACorrection()
        {
            // A client of protocol 30 (or a worker with ReportDroppedTicks off) hears nothing of the dropped ticks.
            using var rig = new PredictedPairRig(lag: 3) { ReportDropped = false };
            rig.Client.Hands = Hands;
            rig.Advance(20);
            int corrections = rig.Client.Corrections;
            rig.SkipWorkerTicks(10);
            rig.Advance(30);
            Assert.Greater(rig.Client.Corrections, corrections);
            Assert.AreEqual(0, rig.Client.Rebases);
        }

        [Test]
        public void AReplayWithheldUnderOverloadIsARebase()
        {
            // e7f5cfe: the worker keeps skipping, so it does not step even an entity that opts in through the ticks it skipped.
            using var rig = new PredictedPairRig(lag: 3);
            rig.Worker.StepSkipped = true;
            rig.Client.Hands = Hands;
            rig.Advance(20);
            int corrections = rig.Client.Corrections;
            PredictedBehaviourBase.ReplaySkippedTicks = false;
            rig.SkipWorkerTicks(6);
            rig.Advance(12);
            PredictedBehaviourBase.ReplaySkippedTicks = true;
            rig.Advance(12);
            Assert.AreEqual(0, rig.Worker.SkippedTicksStepped, "the replay was withheld");
            Assert.AreEqual(corrections, rig.Client.Corrections, "no misprediction is counted");
            Assert.AreEqual(1, rig.Client.Rebases);
            Assert.That(rig.Client.MaxRebaseMagnitude, Is.LessThanOrEqualTo(DroppedMotion(6) + 1e-3f));
        }

        [Test]
        public void ARigidbodyEntitySkippedMidDriveRebasesByTheRealDivergence()
        {
            var objects = new List<GameObject>();
            var scenes = new List<Scene>();
            try
            {
                PairRoller Make(string name)
                {
                    var scene = EditorSceneManager.NewPreviewScene();
                    scenes.Add(scene);
                    var go = new GameObject(name);
                    objects.Add(go);
                    SceneManager.MoveGameObjectToScene(go, scene);
                    var body = go.AddComponent<Rigidbody>();
                    body.useGravity = false;
                    body.interpolation = RigidbodyInterpolation.None;
                    go.AddComponent<SphereCollider>().radius = 0.4f;
                    var roller = go.AddComponent<PairRoller>();
                    roller.Body = body;
                    roller.PhysicsHome = scene;
                    go.GetComponent<NetworkIdentity>().Initialize();
                    return roller;
                }
                var client = Make("client");
                var worker = Make("worker");
                worker.Identity.OwnerClientId = 1;
                worker.Identity.HasAuthority = true;
                client.Hands = Hands;
                const int lag = 3;
                var inputs = new Dictionary<uint, byte[]>();
                int skip = 0;
                void Advance(int ticks)
                {
                    for (int i = 0; i < ticks; i++)
                    {
                        uint step = NetworkTime.Tick + 1;
                        NetworkTime.Tick = step;
                        var input = new NetworkWriter(32);
                        client.ClientPredictTick(step, input);
                        inputs[step] = input.ToArray();
                        if (step <= lag) continue;
                        uint s = step - lag;
                        worker.ServerReceiveInput(s, new NetworkReader(inputs[s]));
                        if (skip > 0) { skip--; NetworkTime.Tick = step; continue; }
                        NetworkTime.Tick = s;
                        worker.NetworkTick(s, NetworkTime.TickInterval);
                        var state = new NetworkWriter(64);
                        worker.WriteOwnerState(state);
                        if (worker.TryGetDroppedTicks(s, out uint from, out uint to)) client.NoteDroppedTicks(from, to);
                        client.ClientReconcile(s, new NetworkReader(state.ToSegment()));
                        NetworkTime.Tick = step;
                    }
                }
                Advance(20);
                int corrections = client.Corrections;
                skip = 10; // mid-drive, the worker misses ten ticks: the owner keeps predicting and sending
                Advance(30);
                Assert.AreEqual(corrections, client.Corrections, "no misprediction is counted");
                Assert.AreEqual(1, client.Rebases);
                Assert.That(client.MaxRebaseMagnitude, Is.LessThanOrEqualTo(DroppedMotion(10) + 1e-3f), "no more than the dropped inputs' own motion");
                Assert.That(Vector3.Distance(client.transform.position, worker.transform.position + Hands * NetworkTime.TickInterval * lag), Is.LessThan(1e-2f),
                    "the owner predicts on from the worker's state");
            }
            finally
            {
                foreach (var go in objects) if (go != null) Object.DestroyImmediate(go);
                foreach (var s in scenes) EditorSceneManager.ClosePreviewScene(s);
            }
        }
    }
}
