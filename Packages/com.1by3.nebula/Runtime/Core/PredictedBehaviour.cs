using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>The wire identity of a server event type: a 32-bit FNV-1a hash of its full name, the same in every process of one build.</summary>
    internal static class ServerEventType<TEvent>
    {
        public static readonly uint Id = Hash(typeof(TEvent).FullName ?? typeof(TEvent).Name);

        private static uint Hash(string name)
        {
            uint h = 2166136261u;
            for (int i = 0; i < name.Length; i++) h = (h ^ name[i]) * 16777619u;
            return h;
        }
    }

    /// <summary>Marker for the per-tick input struct a predicted entity consumes.</summary>
    public interface INetworkInput : INetworkSerializable { }

    /// <summary>Non-generic surface Nebula's worker and client drive; game code derives from <see cref="PredictedBehaviour{TInput}"/>.</summary>
    [RequireComponent(typeof(NetworkTransform))]
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

        // ---- correction smoothing -----------------------------------------------------------------------------

        /// <summary>
        /// Owning client: how long a correction takes to fade from the screen, in seconds. When the worker's state
        /// disagrees with the prediction, the simulated state moves to the corrected one at once (the replay is
        /// exact), but the entity is <i>drawn</i> at an offset from it that starts at where it was last drawn and
        /// decays exponentially: the offset falls to 1% of its size after this long. Offsets from corrections that
        /// arrive faster than they decay add up. Default 0.1 (100 ms); 0 or less turns smoothing off, so the drawn
        /// pose snaps with the simulated one.
        /// </summary>
        protected virtual float CorrectionSmoothingSeconds => 0.1f;

        /// <summary>
        /// Owning client: a correction that would leave the drawn pose further than this from the simulated one, in meters, is not
        /// smoothed; the entity teleports (a respawn, a long server-side move). Default 3.
        /// </summary>
        protected virtual float CorrectionSnapDistance => 3f;

        // The offset from the simulated pose to the drawn one, in _offsetContainer's local space: drawn position = simulated
        // position + offset, drawn rotation = rotation offset * simulated rotation. Container-local, so it follows the
        // container while a ship carries the entity around.
        private Vector3 _drawnOffset;
        private Quaternion _drawnRotationOffset = Quaternion.identity;
        private Container _offsetContainer;
        private bool _offsetApplied;
        private bool _offsetFresh;
        // What ApplyDrawnOffset found and left, so RemoveDrawnOffset can put the simulated pose back bit for bit instead of
        // subtracting the offset again (the rounding of each frame's add and subtract would otherwise seep into the simulation).
        private Container _appliedContainer;
        private Vector3 _simulatedPosition, _drawnPositionLocal;
        private Quaternion _simulatedRotation, _drawnRotationLocal;

        /// <summary>The decay constant ln(100): <see cref="CorrectionSmoothingSeconds"/> is the time the offset takes to fall to 1%.</summary>
        private const float DecayLn = 4.60517f;
        private const float OffsetEpsilonMeters = 0.001f;
        private const float OffsetEpsilonDegrees = 0.05f;

        /// <summary>Owning client: the offset from the simulated position to the drawn one, in the current container's local space (zero when nothing is being eased).</summary>
        public Vector3 CorrectionOffset { get { ConvertOffsetToCurrentContainer(); return _drawnOffset; } }
        /// <summary>Owning client: the rotation that takes the simulated rotation to the drawn one, in the current container's local space.</summary>
        public Quaternion CorrectionRotationOffset { get { ConvertOffsetToCurrentContainer(); return _drawnRotationOffset; } }
        /// <summary>Owning client: the size of <see cref="CorrectionOffset"/> in meters, for diagnostics.</summary>
        public float CorrectionOffsetMeters => CorrectionOffset.magnitude;
        /// <summary>Owning client: the angle of <see cref="CorrectionRotationOffset"/> in degrees, for diagnostics.</summary>
        public float CorrectionOffsetDegrees => Quaternion.Angle(Quaternion.identity, CorrectionRotationOffset);

        /// <summary>
        /// Owning client: the world position the entity is drawn at, which is the simulated position plus the
        /// correction offset still being eased. Cameras and anything else that follows the entity on screen should
        /// read this, or <c>transform.position</c> during a frame, where Nebula has already moved the transform to
        /// the drawn pose (it moves it back before the next <c>Simulate</c>). Equal to <c>transform.position</c> when nothing is being eased.
        /// </summary>
        public Vector3 DrawnPosition
        {
            get
            {
                if (_offsetApplied || !HasCorrectionOffset) return Identity.transform.position;
                ConvertOffsetToCurrentContainer();
                var container = Identity.Container;
                return Identity.transform.position + (container != null ? container.Rotation * _drawnOffset : _drawnOffset);
            }
        }

        /// <summary>Owning client: the world rotation the entity is drawn with; see <see cref="DrawnPosition"/>.</summary>
        public Quaternion DrawnRotation
        {
            get
            {
                if (_offsetApplied || !HasCorrectionOffset) return Identity.transform.rotation;
                ConvertOffsetToCurrentContainer();
                var container = Identity.Container;
                if (container == null) return _drawnRotationOffset * Identity.transform.rotation;
                var c = container.Rotation;
                return c * _drawnRotationOffset * Quaternion.Inverse(c) * Identity.transform.rotation;
            }
        }

        private bool HasCorrectionOffset => _drawnOffset.sqrMagnitude > 0f || _drawnRotationOffset != Quaternion.identity;

        private void ConvertOffsetToCurrentContainer()
        {
            var to = Identity.Container;
            if (_offsetContainer == to) return;
            var from = _offsetContainer;
            _offsetContainer = to;
            if (!HasCorrectionOffset) return;
            var convert = (to != null ? to.InverseRotation : Quaternion.identity) * (from != null ? from.Rotation : Quaternion.identity);
            _drawnOffset = convert * _drawnOffset;
            _drawnRotationOffset = convert * _drawnRotationOffset * Quaternion.Inverse(convert);
        }

        /// <summary>
        /// Put the entity back at its simulated pose if <see cref="ApplyDrawnOffset"/> moved it to the drawn one. Everything
        /// that simulates, reconciles or moves the entity calls this first, so <c>Simulate</c> never sees the offset.
        /// </summary>
        internal void RemoveDrawnOffset()
        {
            if (!_offsetApplied) return;
            _offsetApplied = false;
            var container = Identity.Container;
            var position = Identity.LocalPosition;
            var rotation = Identity.LocalRotation;
            if (container == _appliedContainer && position.Equals(_drawnPositionLocal) && rotation.Equals(_drawnRotationLocal))
            {
                // Nothing moved it since it was drawn: back to exactly where the simulation left it.
                Identity.SetLocalPose(container, _simulatedPosition, _simulatedRotation);
                return;
            }
            // Something else moved it in between (or it changed container): take the offset off where it is now.
            ConvertOffsetToCurrentContainer();
            Identity.SetLocalPose(container, position - _drawnOffset, Quaternion.Inverse(_drawnRotationOffset) * rotation);
        }

        /// <summary>
        /// Once a frame, after the frame's messages and before anything draws: decay the offset by <paramref name="deltaTime"/>
        /// seconds and move the entity to the simulated pose plus what is left of it. A correction made during this frame is
        /// drawn in full first and decays from the next frame on.
        /// </summary>
        internal void ApplyDrawnOffset(float deltaTime)
        {
            RemoveDrawnOffset();
            if (!HasCorrectionOffset) return;
            ConvertOffsetToCurrentContainer();
            float seconds = CorrectionSmoothingSeconds;
            if (seconds <= 0f) { ClearCorrectionOffset(); return; }
            if (_offsetFresh) _offsetFresh = false;
            else
            {
                float k = Mathf.Exp(-DecayLn * Mathf.Max(0f, deltaTime) / seconds);
                _drawnOffset *= k;
                _drawnRotationOffset = Quaternion.Slerp(Quaternion.identity, _drawnRotationOffset, k);
            }
            if (_drawnOffset.sqrMagnitude < OffsetEpsilonMeters * OffsetEpsilonMeters && CorrectionOffsetDegrees < OffsetEpsilonDegrees)
            {
                ClearCorrectionOffset();
                return;
            }
            _appliedContainer = Identity.Container;
            _simulatedPosition = Identity.LocalPosition;
            _simulatedRotation = Identity.LocalRotation;
            Identity.SetLocalPose(_appliedContainer, _simulatedPosition + _drawnOffset, _drawnRotationOffset * _simulatedRotation);
            _drawnPositionLocal = Identity.LocalPosition;
            _drawnRotationLocal = Identity.LocalRotation;
            _offsetApplied = true;
        }

        /// <summary>Owning client: drop the correction offset, so the entity is drawn at its simulated pose from now on (a teleport the game wants shown at once).</summary>
        public void ClearCorrectionOffset()
        {
            RemoveDrawnOffset();
            _drawnOffset = Vector3.zero;
            _drawnRotationOffset = Quaternion.identity;
            _offsetContainer = Identity != null ? Identity.Container : null;
            _offsetFresh = false;
        }

        /// <summary>
        /// A correction moved the simulated pose from (<paramref name="previousPosition"/>, <paramref name="previousRotation"/>), in
        /// <paramref name="previousContainer"/>, to where it is now. The entity keeps being drawn where it was: the offset becomes
        /// the old drawn pose minus the new simulated one, on top of whatever was still being eased, unless that is further than
        /// <see cref="CorrectionSnapDistance"/> (or smoothing is off), when the drawn pose follows at once.
        /// </summary>
        internal void NoteCorrection(Container previousContainer, Vector3 previousPosition, Quaternion previousRotation)
        {
            RemoveDrawnOffset();
            var container = Identity.Container;
            var position = Identity.LocalPosition;
            var rotation = Identity.LocalRotation;
            if (previousContainer != container)
            {
                var world = previousContainer != null ? previousContainer.ToWorld(previousPosition) : previousPosition;
                previousPosition = container != null ? container.ToLocal(world) : world;
                var worldRotation = previousContainer != null ? previousContainer.Rotation * previousRotation : previousRotation;
                previousRotation = container != null ? container.InverseRotation * worldRotation : worldRotation;
            }
            ConvertOffsetToCurrentContainer();
            var offset = _drawnOffset + (previousPosition - position);
            if (CorrectionSmoothingSeconds <= 0f || offset.magnitude > CorrectionSnapDistance)
            {
                ClearCorrectionOffset();
                return;
            }
            _drawnOffset = offset;
            _drawnRotationOffset = (_drawnRotationOffset * previousRotation * Quaternion.Inverse(rotation)).normalized;
            _offsetFresh = true;
        }

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

        /// <summary>
        /// The driver of this entity changed (<see cref="NetworkIdentity.SetDriver"/>): <paramref name="current"/> is the
        /// new driver's client id, 0 when nobody drives it now. Called on the worker that has authority when the
        /// change is made, and on every client that holds the entity when it hears of it, the new and the previous
        /// driver's included. Use it for what the seat does (a camera, a HUD, handing the controls back to the
        /// worker's own logic); Nebula has already switched the input source and the prediction.
        /// </summary>
        protected virtual void OnDriverChanged(ulong previous, ulong current) { }

        /// <summary>
        /// Ticks without any input from the driver after which the worker stops repeating the last one and runs
        /// <see cref="PredictedBehaviour{TInput}.GatherServerInput"/> instead, until the driver's input comes back: a
        /// driver whose link drops does not leave the vehicle at full throttle. Default 30 (half a second). The driver
        /// keeps the seat; take it with <see cref="NetworkIdentity.ClearDriver"/> when the player leaves.
        /// </summary>
        protected virtual int DriverInputTimeoutTicks => 30;

        internal int DriverTimeoutTicks => DriverInputTimeoutTicks;

        // ---- server events ---------------------------------------------------------------------------------------

        /// <summary>The most server events that can apply on one tick of one entity. More are refused and counted in <see cref="ServerEventsDropped"/>.</summary>
        public const int MaxServerEventsPerTick = 4;

        /// <summary>
        /// For how many ticks after the tick it applies on a server event keeps being sent in the owner's reports, so a lost packet does
        /// not lose it. The owner ignores the repeats.
        /// </summary>
        public const int ServerEventRepeatTicks = 30;

        /// <summary>Authority: server events accepted by <see cref="PostServerEvent{TEvent}"/> since spawn.</summary>
        public int ServerEventsPosted { get; private set; }
        /// <summary>Authority: server events <see cref="PostServerEvent{TEvent}"/> refused because the tick they would apply on already held <see cref="MaxServerEventsPerTick"/>.</summary>
        public int ServerEventsDropped { get; private set; }
        /// <summary>Owning client: times a report made the prediction replay because it carried an event for a tick the client had already simulated without it.</summary>
        public int ServerEventReplays { get; private protected set; }

        private protected struct ServerEventEntry
        {
            public uint Tick;
            public byte Index;
            public uint Type;
            public byte[] Payload;
            public bool Applied;
        }

        // The events this copy knows of: on the authority the ones posted and not yet old enough to stop being reported,
        // on the owning client the ones its reports carried, applied or not.
        private protected readonly List<ServerEventEntry> _serverEvents = new List<ServerEventEntry>();
        private static readonly NetworkWriter ServerEventWriter = new NetworkWriter(128);
        private static readonly NetworkReader ServerEventReader = new NetworkReader();
        private static readonly NetworkReader ServerEventListReader = new NetworkReader();
        private protected uint _serverEventTick;
        private protected bool _serverEventsVisible;
        // Authority: the newest tick NetworkTick has run. Events stamped at or before it are never shown to Simulate again.
        private protected uint _lastServerTick;
        // Owning client: the newest tick reconciled; events much older than it are not kept.
        private protected uint _serverEventFloor;
        private bool _warnedPostWithoutAuthority;

        /// <summary>
        /// Authority only: attach an event to the next tick this entity simulates, so its <c>Simulate</c> sees it there with
        /// <see cref="TryGetServerEvent{TEvent}(out TEvent)"/>. The owner's client is sent the event with its state reports and keeps it, so
        /// its prediction replays the event on the same tick, and it travels with the entity when another worker takes it over, without
        /// applying twice. Use it for what the worker decides and the owner cannot know: a shove, a stun, a forced move.
        /// <para>
        /// At most <see cref="MaxServerEventsPerTick"/> events apply on one tick; the next is refused, counted in
        /// <see cref="ServerEventsDropped"/>, and this returns false. It also returns false, with a warning once, on a copy without authority.
        /// <typeparamref name="TEvent"/> is identified on the wire by a hash of its full name, so use a distinct struct for each kind of event.
        /// </para>
        /// </summary>
        public bool PostServerEvent<TEvent>(in TEvent e) where TEvent : struct, INetworkSerializable
        {
            if (Identity == null || !Identity.HasAuthority)
            {
                if (!_warnedPostWithoutAuthority)
                {
                    _warnedPostWithoutAuthority = true;
                    NebulaLog.Warn($"PostServerEvent<{typeof(TEvent).Name}> on {Identity}: only the authoritative worker can post server events; ignored");
                }
                return false;
            }
            // The next tick this entity simulates: the one in progress when it has not run yet this tick, otherwise the one after.
            uint tick = Math.Max(_lastServerTick + 1, NetworkTime.Tick);
            int sameTick = 0;
            for (int i = 0; i < _serverEvents.Count; i++) if (_serverEvents[i].Tick == tick) sameTick++;
            if (sameTick >= MaxServerEventsPerTick)
            {
                ServerEventsDropped++;
                return false;
            }
            ServerEventWriter.Reset();
            e.Serialize(ServerEventWriter);
            _serverEvents.Add(new ServerEventEntry { Tick = tick, Index = (byte)sameTick, Type = ServerEventType<TEvent>.Id, Payload = ServerEventWriter.ToArray() });
            ServerEventsPosted++;
            return true;
        }

        /// <summary>
        /// Inside <c>Simulate</c>: the server event of type <typeparamref name="TEvent"/> posted for the tick being simulated, if any. It is there
        /// on the authoritative worker, in the owner's replay of that tick and in the owner's own simulation of it once its client
        /// has heard of the event. Apply it to simulated state (velocity, a stun timer: whatever <c>WriteState</c> carries) so that the
        /// owner's replay ends where the worker did. Outside <c>Simulate</c> it finds nothing.
        /// </summary>
        protected bool TryGetServerEvent<TEvent>(out TEvent e) where TEvent : struct, INetworkSerializable => TryGetServerEvent(0, out e);

        /// <summary>The <paramref name="index"/>th (from 0, in the order they were posted) server event of type <typeparamref name="TEvent"/> for the tick being simulated; see <see cref="TryGetServerEvent{TEvent}(out TEvent)"/>.</summary>
        protected bool TryGetServerEvent<TEvent>(int index, out TEvent e) where TEvent : struct, INetworkSerializable
        {
            e = default;
            if (!_serverEventsVisible || _serverEvents.Count == 0 || index < 0) return false;
            uint type = ServerEventType<TEvent>.Id;
            int seen = 0;
            for (int i = 0; i < _serverEvents.Count; i++)
            {
                var entry = _serverEvents[i];
                if (entry.Tick != _serverEventTick || entry.Type != type || seen++ != index) continue;
                ServerEventReader.Set(new ArraySegment<byte>(entry.Payload));
                try
                {
                    e = new TEvent();
                    e.Deserialize(ServerEventReader);
                    return true;
                }
                catch (Exception ex)
                {
                    NebulaLog.Error($"{typeof(TEvent).Name} server event on {Identity} could not be read: {ex.Message}");
                    e = default;
                    return false;
                }
            }
            return false;
        }

        /// <summary>The authority is about to simulate <paramref name="tick"/>: show Simulate its events, once, and forget the ones too old to repeat.</summary>
        private protected void BeginServerEventTick(uint tick)
        {
            bool first = tick > _lastServerTick;
            if (first) _lastServerTick = tick;
            _serverEventTick = tick;
            _serverEventsVisible = first && _serverEvents.Count > 0;
            for (int i = _serverEvents.Count - 1; i >= 0; i--)
                if (_serverEvents[i].Tick + (uint)ServerEventRepeatTicks < tick) _serverEvents.RemoveAt(i);
        }

        /// <summary>The client is about to simulate (or replay) <paramref name="tick"/>.</summary>
        private protected void BeginClientEventTick(uint tick)
        {
            _serverEventTick = tick;
            _serverEventsVisible = _serverEvents.Count > 0;
        }

        /// <summary>Simulate for <paramref name="tick"/> returned: events at that tick have now been applied here.</summary>
        private protected void EndEventTick(uint tick)
        {
            _serverEventsVisible = false;
            for (int i = 0; i < _serverEvents.Count; i++)
            {
                if (_serverEvents[i].Tick != tick || _serverEvents[i].Applied) continue;
                var entry = _serverEvents[i];
                entry.Applied = true;
                _serverEvents[i] = entry;
            }
        }

        /// <summary>Owning client: whether an event the client has not simulated yet falls after <paramref name="serverTick"/> and no later than <paramref name="clientTick"/>.</summary>
        private protected bool HasUnappliedServerEvents(uint serverTick, uint clientTick)
        {
            for (int i = 0; i < _serverEvents.Count; i++)
            {
                var entry = _serverEvents[i];
                if (!entry.Applied && entry.Tick > serverTick && entry.Tick <= clientTick) return true;
            }
            return false;
        }

        /// <summary>Owning client: <paramref name="serverTick"/> has been reconciled; forget the events that no replay can reach any more.</summary>
        private protected void NoteServerEventsReconciled(uint serverTick)
        {
            _serverEventFloor = serverTick;
            for (int i = _serverEvents.Count - 1; i >= 0; i--)
                if (_serverEvents[i].Tick + (uint)ServerEventRepeatTicks * 2 < serverTick) _serverEvents.RemoveAt(i);
        }

        private protected void ClearServerEvents()
        {
            _serverEvents.Clear();
            _serverEventsVisible = false;
        }

        /// <summary>The events to send with this tick's owner state, or null when there are none. Authority only; see <see cref="OwnerStateMsg.Events"/>.</summary>
        internal byte[] TakeServerEventsForReport()
        {
            if (_serverEvents.Count == 0) return null;
            ServerEventWriter.Reset();
            WriteServerEventList(ServerEventWriter);
            return ServerEventWriter.ToArray();
        }

        private protected void WriteServerEventList(NetworkWriter writer)
        {
            int count = Math.Min(255, _serverEvents.Count);
            writer.WriteByte((byte)count);
            for (int i = 0; i < count; i++)
            {
                var entry = _serverEvents[i];
                writer.WriteUInt(entry.Tick);
                writer.WriteByte(entry.Index);
                writer.WriteUInt(entry.Type);
                writer.WriteBytes(entry.Payload);
            }
        }

        /// <summary>Add the events of a list written by <see cref="WriteServerEventList"/> that this copy does not hold yet.</summary>
        private protected void ReadServerEventList(NetworkReader reader)
        {
            int count = reader.ReadByte();
            for (int n = 0; n < count; n++)
            {
                uint tick = reader.ReadUInt();
                byte index = reader.ReadByte();
                uint type = reader.ReadUInt();
                var payload = reader.ReadBytes();
                bool known = false;
                for (int i = 0; i < _serverEvents.Count && !known; i++) known = _serverEvents[i].Tick == tick && _serverEvents[i].Index == index;
                // A repeat, an event for a tick no replay can start before, or more than a sender can have held at once.
                if (known || index >= MaxServerEventsPerTick || tick + (uint)ServerEventRepeatTicks * 2 < _serverEventFloor) continue;
                if (_serverEvents.Count >= MaxServerEventsPerTick * (ServerEventRepeatTicks * 2 + 8)) continue;
                _serverEvents.Add(new ServerEventEntry { Tick = tick, Index = index, Type = type, Payload = payload });
            }
        }

        /// <summary>
        /// Owning client: take the server events a report carried (<see cref="OwnerStateMsg.Events"/>) before its state is reconciled, so the replay
        /// that state starts can apply them on their ticks.
        /// </summary>
        internal void ReceiveServerEvents(byte[] events)
        {
            if (events == null || events.Length == 0) return;
            ServerEventListReader.Set(new ArraySegment<byte>(events));
            try { ReadServerEventList(ServerEventListReader); }
            catch (Exception ex) { NebulaLog.Error($"server events for {Identity} could not be read: {ex.Message}"); }
        }

        /// <summary>
        /// The entity went through a portal (<c>docs/portals.md</c> D18): the positions the client predicted in the scope it left are
        /// rewritten into <paramref name="destination"/> through the link's pose, so reconciling the ticks around the crossing compares like with like.
        /// </summary>
        internal abstract void CarryThroughPortal(in PortalMap map, Container destination);

        internal abstract void DriverChanged(ulong previous, ulong current, bool authority);
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
        private uint _lastTickedServerTick;
        private uint _clientTick;
        private uint _lastReconciledTick;
        /// <summary>Server: the tick of the newest real input from the client in control, or the tick control changed hands.</summary>
        private uint _lastControllerInputTick;

        /// <summary>The input consumed by the most recent <see cref="Simulate"/> call.</summary>
        public TInput LastInput { get; private set; }

        /// <summary>Positional error above which the client snaps to the server's state and replays.</summary>
        protected virtual float CorrectionThreshold => 0.05f;
        /// <summary>Diagnostics: log every correction with the server and predicted positions (owning client).</summary>
        public static bool LogCorrections;

        /// <summary>
        /// True while <see cref="Simulate"/> is re-running already-predicted ticks after a server correction. One-shot
        /// side effects that are not part of the simulated state (tracers, sounds) should be skipped while this is set.
        /// </summary>
        protected bool IsReplaying { get; private set; }

        /// <summary>Owning client only: sample the input for this tick.</summary>
        protected abstract TInput GatherInput();

        /// <summary>
        /// Authoritative worker only, for entities with no owning client (NPCs), and for a driven entity while nobody
        /// drives it or its driver's input has stopped for <see cref="PredictedBehaviourBase.DriverInputTimeoutTicks"/>:
        /// produce the input for this tick. The same <see cref="Simulate"/> then runs on it, so an NPC moves, shoots
        /// and hands over exactly like a player. Default: no input.
        /// </summary>
        protected virtual TInput GatherServerInput(uint tick) => default;

        /// <summary>Advance the entity by one tick. Must be a pure function of (current state, input): it runs on the
        /// server, on the predicting client, and again on the client during a replay.</summary>
        protected abstract void Simulate(uint tick, in TInput input, float deltaTime);

        /// <summary>
        /// State the owner needs to reconcile, and the next worker needs to carry on simulating after a handover (it
        /// travels with the pending inputs, and <see cref="ReadState"/> applies it there). Include everything
        /// <see cref="Simulate"/> keeps from one tick to the next. Default: position and rotation in the current container's local
        /// space, and velocity. Container-local because the worker and the owning client never see a moving
        /// container (a ship) at the same place at the same moment; a passenger's pose relative to the ship is what
        /// both agree on.
        /// </summary>
        protected virtual void WriteState(NetworkWriter writer)
        {
            writer.WriteVector3(Identity.LocalPosition);
            writer.WriteQuaternion(Identity.LocalRotation);
            writer.WriteVector3(Identity.Motion.Velocity);
        }

        protected virtual void ReadState(NetworkReader reader)
        {
            var position = reader.ReadVector3();
            var rotation = reader.ReadQuaternion();
            Identity.SetLocalPose(Identity.Container, position, rotation);
            Identity.Motion.Velocity = reader.ReadVector3();
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
            // The client in control: the owner of a pawn, or the driver of a server-owned entity (docs/driven-vehicles.md).
            bool driven = Identity.OwnerClientId == 0 && Identity.DriverClientId != 0;
            bool fresh = slot.Valid && slot.Tick == tick;
            if (Identity.OwnerClientId == 0 && (!driven || (!fresh && (!_hasLastServerInput || DriverSilent(tick)))))
            {
                // Nobody will ever send input for this entity, its driver's first input has not arrived yet, or its
                // driver has gone quiet: the worker is its brain until the driver's input comes.
                input = GatherServerInput(tick);
                LastProcessedInputTick = tick;
                _hasLastServerInput = false;
            }
            else if (fresh)
            {
                input = slot.Input;
                slot.Valid = false;
                LastProcessedInputTick = tick;
                ProcessedInputThisTick = true;
                _lastServerInput = input;
                _hasLastServerInput = true;
                _lastControllerInputTick = tick;
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
            _lastTickedServerTick = tick;
            BeginServerEventTick(tick);
            try { Simulate(tick, in input, deltaTime); }
            finally { _serverEventsVisible = false; }
        }

        /// <summary>A driver sent nothing for this tick and nothing for <see cref="PredictedBehaviourBase.DriverInputTimeoutTicks"/> before it.</summary>
        private bool DriverSilent(uint tick) => tick > _lastControllerInputTick && tick - _lastControllerInputTick > (uint)Math.Max(1, DriverTimeoutTicks);

        internal sealed override void CarryThroughPortal(in PortalMap map, Container destination)
        {
            // A portal is a teleport: nothing is eased across it.
            ClearCorrectionOffset();
            for (int i = 0; i < BufferSize; i++)
            {
                if (!_history[i].Valid) continue;
                ulong scope = _history[i].Container != null ? _history[i].Container.InstanceId : 0UL;
                if (scope != map.FromScope) continue;
                Portals.CarryLocal(in map, _history[i].Container, _history[i].Position, Quaternion.identity, Vector3.zero, destination,
                    out var position, out _, out _);
                _history[i].Position = position;
                _history[i].Container = destination;
            }
        }

        internal sealed override void DriverChanged(ulong previous, ulong current, bool authority)
        {
            if (authority)
            {
                // The new driver's inputs start from nothing: nothing the previous one sent is run for it, and it is
                // given the timeout from now to send its first.
                for (int i = 0; i < BufferSize; i++) _serverInputs[i].Valid = false;
                _hasLastServerInput = false;
                _newestServerInputTick = 0;
                _lastControllerInputTick = NetworkTime.Tick;
            }
            else
            {
                // A client that starts or stops predicting it starts from the worker's state, not from an old history.
                for (int i = 0; i < BufferSize; i++) _history[i].Valid = false;
                _lastReconciledTick = 0;
                ClearCorrectionOffset();
                ClearServerEvents();
            }
            try { OnDriverChanged(previous, current); }
            catch (Exception ex) { NebulaLog.Error($"OnDriverChanged of {GetType().Name} on {Identity} threw: {ex}"); }
        }

        internal sealed override void ServerReceiveInput(uint tick, NetworkReader payload)
        {
            var input = new TInput();
            input.Deserialize(payload);
            if (tick <= LastProcessedInputTick && LastProcessedInputTick != 0) return; // already simulated past it
            ref var slot = ref _serverInputs[tick % BufferSize];
            // A redundant resend of an input still waiting for its tick; one for a tick already run (a missed tick
            // repeated the last input) is stored as before but is too late to announce.
            bool announce = Identity.HasAuthority && !(slot.Valid && slot.Tick == tick) && !(_lastTickedServerTick != 0 && tick <= _lastTickedServerTick);
            slot.Tick = tick;
            slot.Input = input;
            slot.Valid = true;
            if (tick > _newestServerInputTick) _newestServerInputTick = tick;
            if (!announce) return;
            try { OnServerInputReceived(tick, in input); }
            catch (Exception ex) { NebulaLog.Error($"OnServerInputReceived of {GetType().Name} on {Identity} threw: {ex}"); }
        }

        /// <summary>
        /// Authority only: called once for each input from the owner (or driver) the first time it arrives, with the
        /// tick it belongs to, before that tick is simulated. The input waits in the buffer for its tick, which
        /// is usually a few ticks after it arrives, so this is the earliest the game can see what the player did.
        /// <para>
        /// It is not called for a redundant resend of an input already received, for an input that arrives after its
        /// tick has run or has been simulated, for inputs carried in a handover from another worker, or on a copy
        /// without authority. Nothing is called on the client.
        /// </para>
        /// <para>
        /// Read-only: do not change simulated state here, because the input has not been simulated yet and
        /// <see cref="Simulate"/> is the only place that state may change. Use it for side-channel work such as a
        /// provisional acknowledgement to the owner. An exception thrown here is logged and does not affect input handling.
        /// </para>
        /// </summary>
        protected virtual void OnServerInputReceived(uint tick, in TInput input) { }

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
            // The simulation state itself: WriteState is everything Simulate carries from one tick to the next, so
            // the next worker continues exactly where this one stopped - a pawn mid-jump keeps its vertical speed and
            // its air state across a seam instead of starting over from what the ghost happened to hold.
            int stateAt = writer.ReserveUShort();
            int stateStart = writer.Length;
            WriteState(writer);
            writer.PatchUShort(stateAt, (ushort)(writer.Length - stateStart));
            // Server events not yet worn out go with the entity, with the newest tick shown to Simulate: the next worker applies each once.
            if (_serverEvents.Count > 0)
            {
                writer.WriteUInt(_lastServerTick);
                WriteServerEventList(writer);
            }
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
            // A driver's timeout starts again on the worker that takes the entity: its inputs are on their way here.
            _lastControllerInputTick = Math.Max(LastProcessedInputTick, NetworkTime.Tick);
            // A sender from before the state rode along writes nothing more.
            if (reader.Remaining < 2) return;
            var state = reader.ReadSegment(reader.ReadUShort());
            if (state.Count > 0)
            {
                HandoverStateReader.Set(state);
                try { ReadState(HandoverStateReader); }
                catch (Exception ex) { NebulaLog.Error($"ReadState of {GetType().Name} on {Identity} threw during a handover: {ex.Message}"); }
            }
            // A sender without server events to carry writes nothing more.
            if (reader.Remaining >= 5)
            {
                _lastServerTick = Math.Max(_lastServerTick, reader.ReadUInt());
                try { ReadServerEventList(reader); }
                catch (Exception ex) { NebulaLog.Error($"server events of {Identity} could not be read during a handover: {ex.Message}"); }
            }
        }

        private static readonly NetworkReader HandoverStateReader = new NetworkReader();

        // ---- client ------------------------------------------------------------------------------------------

        internal sealed override void ClientPredictTick(uint tick, NetworkWriter inputOut)
        {
            RemoveDrawnOffset();
            var input = GatherInput();
            _clientTick = tick;
            input.Serialize(inputOut);
            LastInput = input;
            BeginClientEventTick(tick);
            try { Simulate(tick, in input, NetworkTime.TickInterval); }
            finally { EndEventTick(tick); }
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
            NoteServerEventsReconciled(serverTick);
            // Simulation works on the simulated pose, never the drawn one.
            RemoveDrawnOffset();
            ref var h = ref _history[serverTick % BufferSize];
            if (!h.Valid || h.Tick != serverTick)
            {
                // Nothing predicted to compare with, so nothing to ease from.
                ClearCorrectionOffset();
                // We have no prediction for that tick (just spawned, just took the wheel of a vehicle, or too old):
                // adopt the server state, and replay what we did predict after it. Adopting alone threw away every
                // tick predicted since: a driver taking a moving vehicle's wheel saw it pulled back by the input lead
                // on every report for a round trip, until the reports reached the ticks it had predicted.
                ReadState(state);
                Replay(serverTick);
                return;
            }
            var previousContainer = Identity.Container;
            var previousPosition = Identity.LocalPosition;
            var previousRotation = Identity.LocalRotation;
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
            if (h.Container != Identity.Container) predicted = Rehome(h.Container, Identity.Container, predicted);
            float error = (serverPos - predicted).magnitude;
            // The prediction agrees with the worker, but the report brought an event for a tick already simulated without it: the
            // worker's state is right, so replay from it, now with the event on its tick, instead of waiting for the error to show.
            bool eventReplay = error <= CorrectionThreshold && HasUnappliedServerEvents(serverTick, _clientTick);
            if (error <= CorrectionThreshold && !eventReplay)
            {
                OwnSnapshotReader.Set(OwnSnapshot.ToSegment());
                ReadState(OwnSnapshotReader);
                return;
            }
            if (eventReplay)
            {
                ServerEventReplays++;
                h.Position = serverPos;
                h.Container = Identity.Container;
                Replay(serverTick);
                NoteCorrection(previousContainer, previousPosition, previousRotation);
                return;
            }
            // Snap to the server's state at serverTick (ReadState left us there) and replay every input after it.
            Corrections++;
            if (LogCorrections) NebulaLog.Info($"correction on {Identity} tick {serverTick} error {error:0.000} m: server {serverPos:F3} predicted {predicted:F3} (recorded in {(h.Container != null ? h.Container.ContainerId : "world")}, now in {(Identity.Container != null ? Identity.Container.ContainerId : "world")})");
            LastCorrectionMagnitude = error;
            if (error > MaxCorrectionMagnitude) MaxCorrectionMagnitude = error;
            h.Position = serverPos;
            h.Container = Identity.Container;
            Replay(serverTick);
            // The simulated state is now exact; the drawn pose eases from where it was.
            NoteCorrection(previousContainer, previousPosition, previousRotation);
            OnCorrected(error);
        }

        /// <summary>
        /// <paramref name="local"/>, a position in <paramref name="from"/>'s local space, in <paramref name="to"/>'s. Two containers of
        /// one physics frame (a ship crossing a planet's chunks, or between a chunk and the planet's own container) are related
        /// through the frame's root, in double, from their local poses: their cached world matrices may have been taken this
        /// frame on either side of the root's move between its drawn and simulated poses (D11), and comparing through them read
        /// the difference as a correction of metres (NEB-478).
        /// </summary>
        private static Vector3 Rehome(Container from, Container to, Vector3 local)
        {
            var root = FrameRootOf(from);
            if (root != null && FrameRootOf(to) == root)
            {
                // What the frame's own container holds hangs under the root: its local position is the root's already.
                var inRoot = from.OwnPhysicsFrame ? Double3.From(local) : PhysicsFrames.PointIn(from.transform, local, root);
                return to.OwnPhysicsFrame ? inRoot.ToVector3() : PhysicsFrames.PointFrom(inRoot, to.transform, root);
            }
            var world = from != null ? from.ToWorld(local) : local;
            return to != null ? to.ToLocal(world) : world;
        }

        /// <summary>The root of the physics frame what <paramref name="c"/> holds lives in (its own, or its space's), when its transform hangs under it; else null.</summary>
        private static Transform FrameRootOf(Container c)
        {
            if (c == null) return null;
            if (c.OwnPhysicsFrame) return c.Frame != null ? c.Frame.Root : null;
            var space = c.Space;
            var root = space != null && space.Frame != null ? space.Frame.Root : null;
            return root != null && PhysicsFrames.IsUnder(c.transform, root) ? root : null;
        }

        /// <summary>Re-run every predicted tick after <paramref name="serverTick"/> from the state just applied, recording where each ended.</summary>
        private void Replay(uint serverTick)
        {
            if (_clientTick <= serverTick) return;
            IsReplaying = true;
            try
            {
                // The ring holds the last BufferSize ticks; anything older was overwritten.
                uint from = Math.Max(serverTick + 1, _clientTick >= BufferSize ? _clientTick - BufferSize + 1 : 0u);
                for (uint t = from; t <= _clientTick; t++)
                {
                    ref var r = ref _history[t % BufferSize];
                    if (!r.Valid || r.Tick != t) continue;
                    BeginClientEventTick(t);
                    try { Simulate(t, in r.Input, NetworkTime.TickInterval); }
                    finally { EndEventTick(t); }
                    r.Position = Identity.LocalPosition;
                    r.Container = Identity.Container;
                }
            }
            finally { IsReplaying = false; }
        }

        internal sealed override void ResetPrediction()
        {
            for (int i = 0; i < BufferSize; i++) { _history[i].Valid = false; _serverInputs[i].Valid = false; }
            _lastReconciledTick = 0;
            _hasLastServerInput = false;
            _newestServerInputTick = 0;
            ClearCorrectionOffset();
            ClearServerEvents();
        }
    }
}
