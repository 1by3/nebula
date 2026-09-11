using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>Marker for the per-tick input struct a predicted entity consumes.</summary>
    public interface INetworkInput : INetworkSerializable { }

    /// <summary>Non-generic surface Nebula's worker and client drive; game code derives from <see cref="PredictedBehaviour{TInput}"/>.</summary>
    public abstract class PredictedBehaviourBase : NetworkBehaviour
    {
        /// <summary>Server: tick of the newest input actually simulated. Reported to the owner for reconciliation.</summary>
        public uint LastProcessedInputTick { get; protected set; }
        /// <summary>Server: true when the most recent tick consumed a real (not repeated) input, i.e. the reported state is comparable to the owner's prediction.</summary>
        public bool ProcessedInputThisTick { get; protected set; }
        public int PendingInputCount { get; protected set; }
        public int InputsMissed { get; protected set; }
        public int Corrections { get; protected set; }
        public float LastCorrectionMagnitude { get; protected set; }
        /// <summary>The largest correction since <see cref="ResetCorrectionStats"/> (or spawn): one big snap hides among many small ones otherwise.</summary>
        public float MaxCorrectionMagnitude { get; protected set; }

        public void ResetCorrectionStats() { Corrections = 0; LastCorrectionMagnitude = 0f; MaxCorrectionMagnitude = 0f; }

        /// <summary>
        /// Server: the smallest input lead (input tick minus the worker's current tick at arrival) seen since the last
        /// <see cref="TakeInputLead"/>. An input only gets simulated when it arrives with a lead of at least 1; the
        /// owner uses this to stretch or relax how far ahead it sends. <see cref="int.MaxValue"/> while nothing arrived.
        /// </summary>
        public int InputLeadMin { get; private set; } = int.MaxValue;

        internal void NoteInputLead(long lead)
        {
            if (lead < InputLeadMin) InputLeadMin = (int)Math.Max(int.MinValue + 1, lead);
        }

        /// <summary>Report the worst lead since the previous report and start a fresh window.</summary>
        internal sbyte TakeInputLead()
        {
            int lead = InputLeadMin;
            InputLeadMin = int.MaxValue;
            if (lead == int.MaxValue) return OwnerStateMsg.NoInputLead;
            return (sbyte)Math.Max(sbyte.MinValue + 1, Math.Min(sbyte.MaxValue, lead));
        }

        internal abstract void ServerReceiveInput(uint tick, NetworkReader payload);
        internal abstract void ClientPredictTick(uint tick, NetworkWriter inputOut);
        internal abstract void ClientReconcile(uint serverTick, NetworkReader state);
        internal abstract void WriteOwnerState(NetworkWriter writer);
        internal abstract void WritePendingInputs(NetworkWriter writer);
        internal abstract void ReadPendingInputs(NetworkReader reader);
        internal abstract void ResetPrediction();
    }

    /// <summary>
    /// A server-authoritative, client-predicted behaviour (the player controller, typically).
    /// <list type="bullet">
    /// <item>On the owning client, <see cref="GatherInput"/> runs every tick, the input is sent and
    /// <see cref="Simulate"/> is run immediately (prediction).</item>
    /// <item>On the authoritative worker, <see cref="Simulate"/> runs with the input for that tick. When the
    /// entity crosses to another worker the buffered inputs travel with it, so the input stream never breaks.</item>
    /// <item>The worker reports its post-simulation state to the owner; if the client's prediction for that tick
    /// disagrees by more than <see cref="CorrectionThreshold"/>, the client rewinds and replays.</item>
    /// </list>
    /// </summary>
    public abstract class PredictedBehaviour<TInput> : PredictedBehaviourBase where TInput : struct, INetworkInput
    {
        private const int BufferSize = 128;
        private static readonly NetworkWriter OwnSnapshot = new NetworkWriter(256);
        private static readonly NetworkReader OwnSnapshotReader = new NetworkReader();

        private struct InputSlot { public uint Tick; public TInput Input; public bool Valid; }
        private struct HistorySlot { public uint Tick; public TInput Input; public Vector3 Position; public Container Container; public bool Valid; }

        private readonly InputSlot[] _serverInputs = new InputSlot[BufferSize];
        private readonly HistorySlot[] _history = new HistorySlot[BufferSize];
        private TInput _lastServerInput;
        private bool _hasLastServerInput;
        private uint _newestServerInputTick;
        private uint _clientTick;
        private uint _lastReconciledTick;

        /// <summary>The input consumed by the most recent <see cref="Simulate"/> call.</summary>
        public TInput LastInput { get; private set; }

        /// <summary>Positional error above which the client snaps to the server's state and replays.</summary>
        protected virtual float CorrectionThreshold => 0.05f;

        /// <summary>
        /// True while <see cref="Simulate"/> is re-running already-predicted ticks after a server correction. One-shot
        /// side effects that are not part of the simulated state (tracers, sounds) should be skipped while this is set.
        /// </summary>
        protected bool IsReplaying { get; private set; }

        /// <summary>Owning client only: sample the input for this tick.</summary>
        protected abstract TInput GatherInput();

        /// <summary>
        /// Authoritative worker only, for entities with no owning client (NPCs): produce the input for this tick.
        /// The same <see cref="Simulate"/> then runs on it, so an NPC moves, shoots and hands over exactly like a
        /// player. Default: no input.
        /// </summary>
        protected virtual TInput GatherServerInput(uint tick) => default;

        /// <summary>Advance the entity by one tick. Must be a pure function of (current state, input): it runs on the
        /// server, on the predicting client, and again on the client during a replay.</summary>
        protected abstract void Simulate(uint tick, in TInput input, float deltaTime);

        /// <summary>
        /// State the owner needs to reconcile. Default: position and rotation in the current container's local
        /// space, and velocity. Container-local because the worker and the owning client never see a moving
        /// container (a ship) at the same place at the same moment; a passenger's pose relative to the ship is what
        /// both agree on.
        /// </summary>
        protected virtual void WriteState(NetworkWriter writer)
        {
            writer.WriteVector3(Identity.LocalPosition);
            writer.WriteQuaternion(Identity.LocalRotation);
            writer.WriteVector3(Identity.Velocity);
        }

        protected virtual void ReadState(NetworkReader reader)
        {
            var position = reader.ReadVector3();
            var rotation = reader.ReadQuaternion();
            Identity.SetLocalPose(Identity.Container, position, rotation);
            Identity.Velocity = reader.ReadVector3();
        }

        /// <summary>Called on the client after a correction was applied (for effects/telemetry).</summary>
        protected virtual void OnCorrected(float magnitude) { }

        /// <summary>The prediction history holds container-local positions, which an origin shift leaves alone; outside every container they are frame positions and follow the shift. Call base when overriding.</summary>
        public override void OnOriginShifted(Vector3 delta)
        {
            if (Container != null) return;
            for (int i = 0; i < BufferSize; i++) if (_history[i].Valid) _history[i].Position += delta;
        }

        // ---- server ------------------------------------------------------------------------------------------

        public sealed override void NetworkTick(uint tick, float deltaTime)
        {
            TInput input;
            ref var slot = ref _serverInputs[tick % BufferSize];
            ProcessedInputThisTick = false;
            if (Identity.OwnerClientId == 0)
            {
                // Nobody will ever send input for this entity: the worker is its brain.
                input = GatherServerInput(tick);
                LastProcessedInputTick = tick;
            }
            else if (slot.Valid && slot.Tick == tick)
            {
                input = slot.Input;
                slot.Valid = false;
                LastProcessedInputTick = tick;
                ProcessedInputThisTick = true;
                _lastServerInput = input;
                _hasLastServerInput = true;
            }
            else if (_hasLastServerInput)
            {
                input = _lastServerInput; // repeat the last input rather than freezing
                InputsMissed++;
            }
            else
            {
                input = default;
            }
            PendingInputCount = (int)Math.Max(0, (long)_newestServerInputTick - tick);
            LastInput = input;
            Simulate(tick, in input, deltaTime);
        }

        internal sealed override void ServerReceiveInput(uint tick, NetworkReader payload)
        {
            var input = new TInput();
            input.Deserialize(payload);
            if (tick <= LastProcessedInputTick && LastProcessedInputTick != 0) return; // already simulated past it
            ref var slot = ref _serverInputs[tick % BufferSize];
            slot.Tick = tick;
            slot.Input = input;
            slot.Valid = true;
            if (tick > _newestServerInputTick) _newestServerInputTick = tick;
        }

        internal sealed override void WriteOwnerState(NetworkWriter writer)
        {
            WriteState(writer);
        }

        internal sealed override void WritePendingInputs(NetworkWriter writer)
        {
            writer.WriteUInt(LastProcessedInputTick);
            int at = writer.ReserveUShort();
            ushort n = 0;
            uint from = LastProcessedInputTick + 1;
            for (uint t = from; t <= _newestServerInputTick && n < BufferSize; t++)
            {
                ref var slot = ref _serverInputs[t % BufferSize];
                if (slot.Valid && slot.Tick == t)
                {
                    writer.WriteUInt(t);
                    slot.Input.Serialize(writer);
                    n++;
                }
            }
            writer.PatchUShort(at, n);
            writer.WriteBool(_hasLastServerInput);
            if (_hasLastServerInput) _lastServerInput.Serialize(writer);
        }

        internal sealed override void ReadPendingInputs(NetworkReader reader)
        {
            LastProcessedInputTick = reader.ReadUInt();
            int n = reader.ReadUShort();
            for (int i = 0; i < n; i++)
            {
                uint t = reader.ReadUInt();
                var input = new TInput();
                input.Deserialize(reader);
                ref var slot = ref _serverInputs[t % BufferSize];
                slot.Tick = t;
                slot.Input = input;
                slot.Valid = true;
                if (t > _newestServerInputTick) _newestServerInputTick = t;
            }
            _hasLastServerInput = reader.ReadBool();
            if (_hasLastServerInput)
            {
                var last = new TInput();
                last.Deserialize(reader);
                _lastServerInput = last;
            }
        }

        // ---- client ------------------------------------------------------------------------------------------

        internal sealed override void ClientPredictTick(uint tick, NetworkWriter inputOut)
        {
            var input = GatherInput();
            _clientTick = tick;
            input.Serialize(inputOut);
            LastInput = input;
            Simulate(tick, in input, NetworkTime.TickInterval);
            ref var h = ref _history[tick % BufferSize];
            h.Tick = tick;
            h.Input = input;
            h.Position = Identity.LocalPosition;
            h.Container = Identity.Container;
            h.Valid = true;
        }

        internal sealed override void ClientReconcile(uint serverTick, NetworkReader state)
        {
            if (serverTick <= _lastReconciledTick) return;
            _lastReconciledTick = serverTick;
            ref var h = ref _history[serverTick % BufferSize];
            if (!h.Valid || h.Tick != serverTick)
            {
                // We have no prediction for that tick (just spawned, or too old): adopt the server state outright.
                ReadState(state);
                return;
            }
            // Peek at the server state without disturbing our own. Everything WriteState covers is saved and put
            // back, not just the pose: a game's extra fields (a weapon cooldown, a trigger counter) belong to the
            // present, and the server's copy describes an older tick. Restoring only the pose used to hand the
            // client a stale cooldown on every clean reconcile, which fired a phantom shot on the next tick.
            OwnSnapshot.Reset();
            WriteState(OwnSnapshot);
            ReadState(state);
            var serverPos = Identity.LocalPosition;
            // The prediction was recorded in the container the client was in at that tick; the worker may have moved
            // the pawn to another (a seam, a ship's door) since. Compare in the worker's frame, or every crossing
            // would look like a snap the size of the distance between the two origins.
            var predicted = h.Position;
            if (h.Container != Identity.Container)
            {
                var world = h.Container != null ? h.Container.ToWorld(predicted) : predicted;
                predicted = Identity.Container != null ? Identity.Container.ToLocal(world) : world;
            }
            float error = (serverPos - predicted).magnitude;
            if (error <= CorrectionThreshold)
            {
                OwnSnapshotReader.Set(OwnSnapshot.ToSegment());
                ReadState(OwnSnapshotReader);
                return;
            }
            // Snap to the server's state at serverTick (ReadState left us there) and replay every input after it.
            Corrections++;
            LastCorrectionMagnitude = error;
            if (error > MaxCorrectionMagnitude) MaxCorrectionMagnitude = error;
            h.Position = serverPos;
            h.Container = Identity.Container;
            IsReplaying = true;
            try
            {
                for (uint t = serverTick + 1; t <= _clientTick; t++)
                {
                    ref var r = ref _history[t % BufferSize];
                    if (!r.Valid || r.Tick != t) continue;
                    Simulate(t, in r.Input, NetworkTime.TickInterval);
                    r.Position = Identity.LocalPosition;
                    r.Container = Identity.Container;
                }
            }
            finally { IsReplaying = false; }
            OnCorrected(error);
        }

        internal sealed override void ResetPrediction()
        {
            for (int i = 0; i < BufferSize; i++) { _history[i].Valid = false; _serverInputs[i].Valid = false; }
            _lastReconciledTick = 0;
            _hasLastServerInput = false;
            _newestServerInputTick = 0;
        }
    }
}
