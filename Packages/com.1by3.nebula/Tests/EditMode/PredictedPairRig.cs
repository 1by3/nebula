using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
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

        protected override float CorrectionSmoothingSeconds => Smoothing;
        protected override float CorrectionSnapDistance => Snap;
        protected override WalkInput GatherInput() => new WalkInput { Move = Hands };

        protected override void Simulate(uint tick, in WalkInput input, float deltaTime)
        {
            Observed.Add(Identity.LocalPosition);
            Identity.SetLocalPose(Identity.Container, Identity.LocalPosition + (input.Move + Drift) * deltaTime, Identity.LocalRotation);
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

        public PredictedPairRig(int lag, float smoothing = 0.1f, float snap = 3f)
        {
            Lag = lag;
            Client = Make("client");
            Worker = Make("worker");
            Worker.Identity.OwnerClientId = 1;
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
                Worker.NetworkTick(s, NetworkTime.TickInterval);
                var state = new NetworkWriter(64);
                Worker.WriteOwnerState(state);
                Client.ClientReconcile(s, new NetworkReader(state.ToSegment()));
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
