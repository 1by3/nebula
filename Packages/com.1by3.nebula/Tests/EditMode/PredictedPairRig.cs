using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>A server event for <see cref="PairWalker"/>: an instant change of velocity, in meters per second.</summary>
    public struct ShoveEvent : INetworkSerializable
    {
        public Vector3 Velocity;
        public void Serialize(NetworkWriter writer) { writer.WriteVector3(Velocity); }
        public void Deserialize(NetworkReader reader) { Velocity = reader.ReadVector3(); }
    }

    /// <summary>What a <see cref="PairWalker"/> is told each tick: a velocity in meters per second.</summary>
    public struct WalkInput : INetworkInput
    {
        public Vector3 Move;
        public void Serialize(NetworkWriter writer) { writer.WriteVector3(Move); }
        public void Deserialize(NetworkReader reader) { Move = reader.ReadVector3(); }
    }

    /// <summary>
    /// A kinematic walker for prediction tests. <see cref="Drift"/> is simulation state that rides in <see cref="WriteState"/>, so
    /// a replay that has learned of it carries on drifting. <see cref="Observed"/> is the pose each <c>Simulate</c> started from,
    /// which is what "the simulated state" means: smoothing must never change it.
    /// </summary>
    public class PairWalker : PredictedBehaviour<WalkInput>
    {
        public Vector3 Hands;
        public Vector3 Drift;
        public float Smoothing = 0.1f;
        public float Snap = 3f;
        public readonly List<Vector3> Observed = new List<Vector3>();
        /// <summary>Every shove <c>Simulate</c> was shown: the tick and whether it was a replay.</summary>
        public readonly List<(uint Tick, bool Replaying)> ShovesSeen = new List<(uint, bool)>();

        /// <summary>True: the worker steps this walker through ticks it skipped (NEB-412). Off by default, like the real thing.</summary>
        public bool StepSkipped;
        protected override bool StepSkippedServerTicks => StepSkipped;
        /// <summary>True: drawn between its last two predicted ticks (<see cref="PredictedBehaviourBase.RenderBetweenTicks"/>).</summary>
        public bool BetweenTicks;
        /// <summary>Degrees of yaw each <c>Simulate</c> adds (not simulation state: both copies must be given the same).</summary>
        public float TurnDegreesPerTick;
        /// <summary>True: <c>Simulate</c> keeps <see cref="NetworkMotionState.Velocity"/> up to date, so the reports carry the speed (speed-aware reconciliation).</summary>
        public bool ReportVelocity;
        /// <summary>Overrides of the speed-aware reconciliation factors; negative keeps Nebula's default.</summary>
        public float ThresholdTickFraction = -1f, SnapSeconds = -1f, SmoothingDoublingSpeed = -1f;
        protected override float CorrectionThresholdTickFraction => ThresholdTickFraction >= 0f ? ThresholdTickFraction : base.CorrectionThresholdTickFraction;
        protected override float CorrectionSnapSeconds => SnapSeconds >= 0f ? SnapSeconds : base.CorrectionSnapSeconds;
        protected override float CorrectionSmoothingDoublingSpeed => SmoothingDoublingSpeed >= 0f ? SmoothingDoublingSpeed : base.CorrectionSmoothingDoublingSpeed;
        protected override bool RenderBetweenTicks => BetweenTicks;
        protected override float CorrectionSmoothingSeconds => Smoothing;
        protected override float CorrectionSnapDistance => Snap;
        protected override WalkInput GatherInput() => new WalkInput { Move = Hands };

        protected override void Simulate(uint tick, in WalkInput input, float deltaTime)
        {
            Observed.Add(Identity.LocalPosition);
            for (int i = 0; TryGetServerEvent<ShoveEvent>(i, out var shove); i++)
            {
                Drift += shove.Velocity;
                ShovesSeen.Add((tick, IsReplaying));
            }
            Identity.SetLocalPose(Identity.Container, Identity.LocalPosition + (input.Move + Drift) * deltaTime, Identity.LocalRotation * Quaternion.Euler(0f, TurnDegreesPerTick, 0f));
            if (ReportVelocity) Identity.Motion.Velocity = input.Move + Drift;
        }

        protected override void WriteState(NetworkWriter writer)
        {
            base.WriteState(writer);
            writer.WriteVector3(Drift);
        }

        protected override void ReadState(NetworkReader reader)
        {
            base.ReadState(reader);
            Drift = reader.ReadVector3();
        }

        /// <summary>The pose the simulation sees: where the entity is with any drawn offset taken off.</summary>
        public Vector3 SimulatedPosition
        {
            get
            {
                RemoveDrawnOffset();
                return Identity.LocalPosition;
            }
        }
    }

    /// <summary>
    /// A worker's copy and an owner's predicted copy of one walker, joined by a link of <see cref="Lag"/> ticks each way folded into
    /// one: at step n the owner predicts tick n and the worker simulates tick n - Lag from the owner's input for it and reports its
    /// state, which the owner reconciles at once. Every step in between is Nebula's own code.
    /// </summary>
    public sealed class PredictedPairRig : IDisposable
    {
        public readonly PairWalker Client;
        public readonly PairWalker Worker;
        public readonly int Lag;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly Dictionary<uint, byte[]> _inputs = new Dictionary<uint, byte[]>();
        private uint _step;
        /// <summary>The state bytes the worker reported for each tick it simulated.</summary>
        public readonly Dictionary<uint, byte[]> States = new Dictionary<uint, byte[]>();
        /// <summary>The report bytes of the events the worker had for each tick it reported.</summary>
        public readonly Dictionary<uint, byte[]> EventReports = new Dictionary<uint, byte[]>();
        /// <summary>Runs after the worker simulated a tick and before it reports it: where game code on the worker posts events.</summary>
        public Action<uint> AfterWorkerTick;
        /// <summary>False: the worker simulates and records its reports, but the owner does not hear them.</summary>
        public bool DeliverReports = true;
        /// <summary>False: the owner is not told which ticks the worker dropped (NEB-484), as a client of protocol 30 is not.</summary>
        public bool ReportDropped = true;

        public PredictedPairRig(int lag, float smoothing = 0.1f, float snap = 3f)
        {
            Lag = lag;
            Client = Make("client");
            Worker = Make("worker");
            Worker.Identity.OwnerClientId = 1;
            Worker.Identity.HasAuthority = true;
            Client.Smoothing = smoothing;
            Client.Snap = snap;
        }

        private PairWalker Make(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            var walker = go.AddComponent<PairWalker>();
            go.GetComponent<NetworkIdentity>().Initialize();
            return walker;
        }

        public uint Step { get { return _step; } }

        private int _skipWorker;

        /// <summary>The worker runs none of the next <paramref name="steps"/> ticks (it fell behind and skipped their numbers), though their inputs still reach it; it carries on from the one after.</summary>
        public void SkipWorkerTicks(int steps) { _skipWorker = steps; }

        /// <summary>Another walker alongside the pair, such as the worker an entity is handed over to.</summary>
        public PairWalker Extra(string name) { return Make(name); }

        /// <summary>The worker's copy is displaced by a server-side event the owner's prediction knows nothing about.</summary>
        public void Shove(Vector3 meters)
        {
            Worker.Identity.SetLocalPose(Worker.Identity.Container, Worker.Identity.LocalPosition + meters, Worker.Identity.LocalRotation);
        }

        /// <summary>One tick and one rendered frame of <paramref name="frameSeconds"/>.</summary>
        public void Advance(float frameSeconds = NetworkTime.TickInterval)
        {
            _step++;
            NetworkTime.Tick = _step;
            var input = new NetworkWriter(32);
            Client.ClientPredictTick(_step, input);
            _inputs[_step] = input.ToArray();
            if (_step > (uint)Lag)
            {
                uint s = _step - (uint)Lag;
                Worker.ServerReceiveInput(s, new NetworkReader(_inputs[s]));
                if (_skipWorker > 0) { _skipWorker--; Client.ApplyDrawnOffset(frameSeconds); return; }
                NetworkTime.Tick = s;
                Worker.NetworkTick(s, NetworkTime.TickInterval);
                AfterWorkerTick?.Invoke(s);
                var state = new NetworkWriter(64);
                Worker.WriteOwnerState(state);
                States[s] = state.ToArray();
                var events = Worker.TakeServerEventsForReport();
                if (events != null) EventReports[s] = events;
                if (DeliverReports)
                {
                    Client.ReceiveServerEvents(events);
                    if (ReportDropped && Worker.TryGetDroppedTicks(s, out uint droppedFrom, out uint droppedTo)) Client.NoteDroppedTicks(droppedFrom, droppedTo);
                    Client.ClientReconcile(s, new NetworkReader(state.ToSegment()));
                }
            }
            Client.ApplyDrawnOffset(frameSeconds);
        }

        public void Advance(int ticks) { for (int i = 0; i < ticks; i++) Advance(); }

        public void Dispose()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
        }
    }
}
