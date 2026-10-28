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

        public void ResetCorrectionStats() { Corrections = 0; LastCorrectionMagnitude = 0f; MaxCorrectionMagnitude = 0f; Rebases = 0; LastRebaseMagnitude = 0f; MaxRebaseMagnitude = 0f; }

        /// <summary>
        /// Owning client (NEB-484): reports that came after ticks the worker did not simulate this entity through, which the client took as its new
        /// base instead of counting a correction. The magnitude is how far the prediction of that tick was from the worker's: the motion of the
        /// inputs the worker dropped, the real divergence, applied at once (it is not eased like a misprediction).
        /// </summary>
        public int Rebases { get; protected set; }
        public float LastRebaseMagnitude { get; protected set; }
        public float MaxRebaseMagnitude { get; protected set; }

        /// <summary>Authority (NEB-484): the latest range of ticks this entity was not stepped through, inclusive (0 and 0: none).</summary>
        internal uint DroppedFrom, DroppedTo;
        /// <summary>Authority (NEB-484): ticks the dropped range is repeated in the owner's reports after it, so a lost report does not lose it.</summary>
        internal const uint DroppedReportTicks = 30;
        /// <summary>Authority (NEB-484): ticks this entity was not stepped through since spawn.</summary>
        public int TicksDropped { get; protected set; }

        /// <summary>Authority (NEB-484): the dropped range to send with the report of <paramref name="tick"/>, while it is recent.</summary>
        internal bool TryGetDroppedTicks(uint tick, out uint from, out uint to)
        {
            from = DroppedFrom;
            to = DroppedTo;
            return to != 0 && tick > to && tick - to <= DroppedReportTicks;
        }

        /// <summary>Owning client (NEB-484): the newest dropped range a report named, and the end of the last one re-based on.</summary>
        private protected uint _droppedFrom, _droppedTo, _rebasedTo;

        /// <summary>Owning client (NEB-484): a report named ticks the worker dropped; the first reconcile after them re-bases.</summary>
        internal void NoteDroppedTicks(uint from, uint to)
        {
            if (to == 0 || to < from || to <= _droppedTo) return;
            _droppedFrom = from;
            _droppedTo = to;
        }

        /// <summary>Owning client (NEB-484): a dropped range not re-based on yet lies before <paramref name="serverTick"/>; it is taken as re-based.</summary>
        private protected bool TakeRebase(uint serverTick)
        {
            if (_droppedTo <= _rebasedTo || serverTick <= _droppedTo) return false;
            _rebasedTo = _droppedTo;
            return true;
        }

        private protected void ResetDroppedTicks() { _droppedFrom = _droppedTo = _rebasedTo = 0; }

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

        // ---- speed-aware reconciliation -------------------------------------------------------------------------

        /// <summary>
        /// Owning client: the speed, in meters per second, that scales the correction threshold, the snap distance and
        /// the smoothing time below. Read on the worker's reported state of the tick being reconciled. Default: the
        /// magnitude of <see cref="NetworkMotionState.Velocity"/>, which the default <c>WriteState</c> carries; a game
        /// whose state keeps its velocity elsewhere overrides this. Return 0 to make all three speed-independent.
        /// </summary>
        protected virtual float CorrectionSpeed => Identity != null ? Identity.Motion.Velocity.magnitude : 0f;

        /// <summary>
        /// Owning client: the correction threshold grows to this fraction of the distance the entity travels in one tick
        /// at <see cref="CorrectionSpeed"/>, when that is larger than <c>CorrectionThreshold</c>. At 400 m/s a tick is
        /// 6.7 m, so a fixed 5 cm threshold replays on errors no player can see. Default 0.25 (no change below about
        /// 12 m/s at 60 Hz); 0 keeps the threshold fixed.
        /// </summary>
        protected virtual float CorrectionThresholdTickFraction => 0.25f;

        /// <summary>
        /// Owning client: the snap distance grows to the distance the entity travels in this many seconds at
        /// <see cref="CorrectionSpeed"/>, when that is larger than <see cref="CorrectionSnapDistance"/>: a 3 m correction is
        /// 7 ms of flight at 400 m/s and should be eased, not snapped. Default 0.25; 0 keeps the snap distance fixed.
        /// </summary>
        protected virtual float CorrectionSnapSeconds => 0.25f;

        /// <summary>
        /// Owning client: the speed, in meters per second, at which the smoothing time has doubled. The smoothing time is
        /// <c>CorrectionSmoothingSeconds × (1 + speed / this)</c>, at most three times <see cref="CorrectionSmoothingSeconds"/>:
        /// a correction at speed is eased over a longer time so the drawn pose does not visibly accelerate. Default 1500
        /// (104 ms at 60 m/s, 127 ms at 400 m/s, 300 ms from 3 km/s, with the 100 ms default); 0 keeps it fixed.
        /// </summary>
        protected virtual float CorrectionSmoothingDoublingSpeed => 1500f;

        /// <summary>Owning client: <see cref="CorrectionSpeed"/> at the most recent reconcile, which sizes the snap distance and smoothing time of the offset it left.</summary>
        public float LastCorrectionSpeed { get; private protected set; }

        /// <summary>The snap distance at <paramref name="speed"/>; see <see cref="CorrectionSnapSeconds"/>.</summary>
        internal float SnapDistanceAt(float speed)
        {
            float snap = CorrectionSnapDistance;
            float seconds = CorrectionSnapSeconds;
            return seconds > 0f && speed > 0f ? Mathf.Max(snap, speed * seconds) : snap;
        }

        /// <summary>The smoothing time at <paramref name="speed"/>; see <see cref="CorrectionSmoothingDoublingSpeed"/>.</summary>
        internal float SmoothingSecondsAt(float speed)
        {
            float seconds = CorrectionSmoothingSeconds;
            float doubling = CorrectionSmoothingDoublingSpeed;
            if (seconds <= 0f || doubling <= 0f || speed <= 0f) return seconds;
            return seconds * Mathf.Min(3f, 1f + speed / doubling);
        }

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
        /// correction offset still being eased (and, with <see cref="RenderBetweenTicks"/>, moved back between the last two
        /// predicted ticks). Cameras and anything else that follows the entity on screen should
        /// read this, or <c>transform.position</c> during a frame, where Nebula has already moved the transform to
        /// the drawn pose (it moves it back before the next <c>Simulate</c>). Equal to <c>transform.position</c> when nothing is being eased.
        /// </summary>
        public Vector3 DrawnPosition
        {
            get
            {
                if (_offsetApplied) return Identity.transform.position;
                bool blending = ComputeTickBlend();
                if (!HasCorrectionOffset && !blending) return Identity.transform.position;
                ConvertOffsetToCurrentContainer();
                var container = Identity.Container;
                var offset = _drawnOffset + _blendOffset;
                return Identity.transform.position + (container != null ? container.Rotation * offset : offset);
            }
        }

        /// <summary>Owning client: the world rotation the entity is drawn with; see <see cref="DrawnPosition"/>.</summary>
        public Quaternion DrawnRotation
        {
            get
            {
                if (_offsetApplied) return Identity.transform.rotation;
                bool blending = ComputeTickBlend();
                if (!HasCorrectionOffset && !blending) return Identity.transform.rotation;
                ConvertOffsetToCurrentContainer();
                var container = Identity.Container;
                var local = _drawnRotationOffset * _blendRotation;
                if (container == null) return local * Identity.transform.rotation;
                var c = container.Rotation;
                return c * local * Quaternion.Inverse(c) * Identity.transform.rotation;
            }
        }

        // ---- between-tick rendering -------------------------------------------------------------------------------

        /// <summary>
        /// Owning client: draw the entity between the poses of its last two predicted ticks instead of at the newest one.
        /// Prediction runs in fixed steps (60 Hz by default), so a fast predicted entity drawn at its newest tick moves in
        /// 16.7 ms steps whatever the frame rate: at 144 Hz three frames show one pose and the fourth jumps, and a frame
        /// with two fixed steps jumps twice as far. With this on, every frame draws the entity at
        /// <c>Lerp(previous tick, newest tick, fraction)</c>, where the fraction is how far the frame is into the current
        /// fixed step (Unity's own <c>RigidbodyInterpolation.Interpolate</c>, done here because a predicted body is
        /// stepped by Nebula and its interpolation must stay off). Rotation is slerped the same way.
        /// <para>
        /// The blend is part of the drawn pose (<see cref="DrawnPosition"/>): Nebula moves the transform there at the end of
        /// its <c>Update</c>, so renderers and a camera that follows the transform in <c>LateUpdate</c> see the blended
        /// pose, and moves it back before the next <c>Simulate</c>, which never sees it. The cost is up to one tick (8 ms on
        /// average at 60 Hz) of extra visual latency on the entity. Nothing is blended across a container change, a
        /// teleport (a move outside <c>Simulate</c>), or a step longer than both <see cref="CorrectionSnapDistance"/> and two ticks at the entity's <c>Motion.Velocity</c>.
        /// Default false: a game that already interpolates its own view (an eye position of its own) keeps doing so.
        /// </para>
        /// </summary>
        protected virtual bool RenderBetweenTicks => false;

        /// <summary>Owning client: how far the last drawn frame was into the current fixed step, 0 to 1 (1: at the newest tick). Diagnostics.</summary>
        public float RenderTickFraction => _tickFraction;

        // The poses after the last two predicted (or replayed) ticks, container-local in _tickPoseContainer.
        private bool _hasTickPose, _hasPreviousTickPose;
        private Container _tickPoseContainer;
        private Vector3 _tickPosition, _previousTickPosition;
        private Quaternion _tickRotation = Quaternion.identity, _previousTickRotation = Quaternion.identity;
        private float _tickFraction = 1f;
        // This frame's between-tick offset from the simulated pose, container-local: drawn = simulated + correction + blend.
        private Vector3 _blendOffset;
        private Quaternion _blendRotation = Quaternion.identity;
        // The between-tick offset ApplyDrawnOffset applied, for taking it off again when something moved the entity in between.
        private Vector3 _appliedBlendOffset;
        private Quaternion _appliedBlendRotation = Quaternion.identity;

        private bool AtTickPose()
        {
            if (Identity.Container != _tickPoseContainer) return false;
            if ((Identity.LocalPosition - _tickPosition).sqrMagnitude > 1e-8f) return false;
            return Mathf.Abs(Quaternion.Dot(Identity.LocalRotation, _tickRotation)) > 0.9999999f;
        }

        private void RecordTickPose(bool keepPrevious)
        {
            var container = Identity.Container;
            if (keepPrevious && _hasTickPose && container == _tickPoseContainer)
            {
                _previousTickPosition = _tickPosition;
                _previousTickRotation = _tickRotation;
                _hasPreviousTickPose = true;
            }
            else _hasPreviousTickPose = false;
            _tickPoseContainer = container;
            _tickPosition = Identity.LocalPosition;
            _tickRotation = Identity.LocalRotation;
            _hasTickPose = true;
        }

        /// <summary>A predicted tick is about to be simulated: a move made since the last one (a teleport) starts the blend afresh.</summary>
        private protected void BeginPredictedTickPose()
        {
            if (!RenderBetweenTicks) return;
            if (!_hasTickPose || !AtTickPose()) RecordTickPose(false);
        }

        /// <summary>A predicted or replayed tick was simulated: its pose becomes the newest, the one before it the previous.</summary>
        private protected void EndPredictedTickPose()
        {
            if (!RenderBetweenTicks) return;
            RecordTickPose(true);
        }

        /// <summary>The simulated pose was replaced (a reconcile): it is the newest tick pose, with nothing before it until the replay adds some.</summary>
        private protected void ResetPredictedTickPose()
        {
            if (!RenderBetweenTicks) { _hasTickPose = _hasPreviousTickPose = false; return; }
            RecordTickPose(false);
        }

        private protected void ClearPredictedTickPose() => _hasTickPose = _hasPreviousTickPose = false;

        /// <summary>This frame's between-tick offset (<see cref="_blendOffset"/>, <see cref="_blendRotation"/>) from the simulated pose; false when there is none.</summary>
        private bool ComputeTickBlend()
        {
            _blendOffset = Vector3.zero;
            _blendRotation = Quaternion.identity;
            if (!RenderBetweenTicks || !_hasPreviousTickPose || _tickFraction >= 1f || Identity == null) return false;
            if (!AtTickPose()) return false;
            // A step much longer than the entity's speed explains is a jump made inside Simulate (a respawn): not blended.
            var step = _tickPosition - _previousTickPosition;
            float longest = Mathf.Max(CorrectionSnapDistance, Identity.Motion.Velocity.magnitude * NetworkTime.TickInterval * 2f);
            if (step.sqrMagnitude > longest * longest) return false;
            float back = 1f - _tickFraction;
            _blendOffset = -step * back;
            _blendRotation = Quaternion.Slerp(_previousTickRotation, _tickRotation, _tickFraction) * Quaternion.Inverse(_tickRotation);
            return _blendOffset.sqrMagnitude > 0f || _blendRotation != Quaternion.identity;
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
            bool sameContainer = container == _appliedContainer;
            ConvertOffsetToCurrentContainer();
            var blend = sameContainer ? _appliedBlendOffset : Vector3.zero;
            var blendRotation = sameContainer ? _appliedBlendRotation : Quaternion.identity;
            Identity.SetLocalPose(container, position - _drawnOffset - blend, Quaternion.Inverse(_drawnRotationOffset * blendRotation) * rotation);
        }

        /// <summary>
        /// Once a frame, after the frame's messages and before anything draws: decay the offset by <paramref name="deltaTime"/>
        /// seconds and move the entity to the simulated pose plus what is left of it. A correction made during this frame is
        /// drawn in full first and decays from the next frame on. <paramref name="tickFraction"/> is how far this frame is into
        /// the current fixed step (0 to 1); with <see cref="RenderBetweenTicks"/> the entity is drawn that far between its
        /// previous and newest predicted tick, and at 1 (the default) at the newest.
        /// </summary>
        internal void ApplyDrawnOffset(float deltaTime, float tickFraction = 1f)
        {
            RemoveDrawnOffset();
            _tickFraction = Mathf.Clamp01(tickFraction);
            bool blending = ComputeTickBlend();
            if (!HasCorrectionOffset && !blending) return;
            if (HasCorrectionOffset)
            {
                ConvertOffsetToCurrentContainer();
                float seconds = SmoothingSecondsAt(LastCorrectionSpeed);
                if (seconds <= 0f) ClearCorrectionOffset();
                else
                {
                    if (_offsetFresh) _offsetFresh = false;
                    else
                    {
                        float k = Mathf.Exp(-DecayLn * Mathf.Max(0f, deltaTime) / seconds);
                        _drawnOffset *= k;
                        _drawnRotationOffset = Quaternion.Slerp(Quaternion.identity, _drawnRotationOffset, k);
                    }
                    if (_drawnOffset.sqrMagnitude < OffsetEpsilonMeters * OffsetEpsilonMeters && CorrectionOffsetDegrees < OffsetEpsilonDegrees)
                        ClearCorrectionOffset();
                }
                if (!HasCorrectionOffset && !blending) return;
            }
            _appliedContainer = Identity.Container;
            _simulatedPosition = Identity.LocalPosition;
            _simulatedRotation = Identity.LocalRotation;
            _appliedBlendOffset = blending ? _blendOffset : Vector3.zero;
            _appliedBlendRotation = blending ? _blendRotation : Quaternion.identity;
            Identity.SetLocalPose(_appliedContainer, _simulatedPosition + _drawnOffset + _appliedBlendOffset,
                _drawnRotationOffset * _appliedBlendRotation * _simulatedRotation);
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
        /// <see cref="CorrectionSnapDistance"/> (scaled by <see cref="CorrectionSnapSeconds"/> at <see cref="LastCorrectionSpeed"/>; or smoothing is off), when the drawn pose follows at once.
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
            if (CorrectionSmoothingSeconds <= 0f || offset.magnitude > SnapDistanceAt(LastCorrectionSpeed))
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

        /// <summary>
        /// Whether the worker, after it skipped tick numbers because it fell too far behind the wall clock, steps
        /// this entity through each skipped tick that has an input waiting before it simulates the next one (NEB-412).
        /// The owner predicted every one of those ticks with its input, so an entity that is left out ends the next
        /// tick short of where its owner has it and is corrected. Return true when <c>Simulate</c> advances the entity
        /// itself and is a pure function of state and input, as the owner's replay already requires: each skipped tick
        /// then runs exactly as the replay runs it and the entity ends where its owner has it. Leave it false when
        /// <c>Simulate</c> relies on the physics engine stepping between ticks (it applies forces or sets a
        /// Rigidbody's velocity): the skipped ticks would apply their forces back to back before one physics step and
        /// the entity would lurch, so it is corrected after a skip instead. Default false.
        /// </summary>
        protected virtual bool StepSkippedServerTicks => false;

        internal bool StepsSkippedServerTicks => StepSkippedServerTicks;

        /// <summary>Authority: ticks run as part of a catch-up after the worker skipped their numbers (NEB-412).</summary>
        public int SkippedTicksStepped { get; protected set; }

        /// <summary>
        /// Whether the authority steps skipped ticks at all (<see cref="StepSkippedServerTicks"/>), set by the worker before each tick:
        /// true after an occasional hitch, false while the worker skips ticks again and again (overloaded). Stepping each pawn through
        /// the ticks the worker could not afford doubled what the pawns cost it once it fell behind, which kept it behind: at 75
        /// players a worker skipping 1.4 ticks per tick spent 2.4 pawn steps per pawn per tick.
        /// </summary>
        internal static bool ReplaySkippedTicks = true;

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

        /// <summary>
        /// Positional error above which the client snaps to the server's state and replays. At speed the threshold in
        /// use grows with <see cref="PredictedBehaviourBase.CorrectionThresholdTickFraction"/>; see <see cref="CorrectionThresholdAt"/>.
        /// </summary>
        protected virtual float CorrectionThreshold => 0.05f;

        /// <summary>The correction threshold at <paramref name="speed"/> meters per second: <see cref="CorrectionThreshold"/>, or the fraction of a tick's travel <see cref="PredictedBehaviourBase.CorrectionThresholdTickFraction"/> asks for when that is larger.</summary>
        public float CorrectionThresholdAt(float speed)
        {
            float threshold = CorrectionThreshold;
            float fraction = CorrectionThresholdTickFraction;
            return fraction > 0f && speed > 0f ? Mathf.Max(threshold, speed * NetworkTime.TickInterval * fraction) : threshold;
        }
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
            // NEB-412: the worker skipped tick numbers (it fell more than MaxLagTicks behind the wall clock). The owner
            // predicted those ticks with the inputs it sent for them; step through each one whose input is waiting,
            // oldest first, so this tick starts from where the owner has the entity instead of a correction.
            // Not while the worker keeps skipping (ReplaySkippedTicks): then every replay adds a tick's work to a worker that could not
            // afford the ticks it skipped. NEB-484: an entity not stepped through them tells its owner which ticks were dropped instead,
            // and the owner re-bases on the next report rather than counting a misprediction.
            bool controlled = Identity.OwnerClientId != 0 || Identity.DriverClientId != 0;
            if (_lastTickedServerTick != 0 && tick > _lastTickedServerTick + 1 && controlled && !(StepSkippedServerTicks && ReplaySkippedTicks))
            {
                DroppedFrom = _lastTickedServerTick + 1;
                DroppedTo = tick - 1;
                TicksDropped += (int)(DroppedTo - DroppedFrom + 1);
            }
            if (_lastTickedServerTick != 0 && tick > _lastTickedServerTick + 1 && StepSkippedServerTicks && ReplaySkippedTicks && controlled)
            {
                uint from = _lastTickedServerTick + 1;
                if (tick - from >= BufferSize) from = tick - (BufferSize - 1);
                for (uint t = from; t < tick; t++)
                {
                    ref var skipped = ref _serverInputs[t % BufferSize];
                    if (!skipped.Valid || skipped.Tick != t) continue;
                    ServerStep(t, deltaTime);
                    SkippedTicksStepped++;
                }
            }
            ServerStep(tick, deltaTime);
        }

        private void ServerStep(uint tick, float deltaTime)
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
            ClearPredictedTickPose();
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
                ClearPredictedTickPose();
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
            BeginPredictedTickPose();
            BeginClientEventTick(tick);
            try { Simulate(tick, in input, NetworkTime.TickInterval); }
            finally { EndEventTick(tick); }
            EndPredictedTickPose();
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
                TakeRebase(serverTick); // NEB-484: adopted already
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
            // The worker's speed at that tick sizes the threshold, and the snap distance and smoothing of any offset left.
            LastCorrectionSpeed = Mathf.Max(0f, CorrectionSpeed);
            float threshold = CorrectionThresholdAt(LastCorrectionSpeed);
            // The prediction was recorded in the container the client was in at that tick; the worker may have moved
            // the pawn to another (a seam, a ship's door) since. Compare in the worker's frame, or every crossing
            // would look like a snap the size of the distance between the two origins.
            var predicted = h.Position;
            if (h.Container != Identity.Container) predicted = Rehome(h.Container, Identity.Container, predicted, serverTick);
            float error = (serverPos - predicted).magnitude;
            // NEB-484: the worker did not simulate this entity through ticks before this one (it skipped their numbers), so its state here
            // is not what the client's inputs for them make: take it as the new base and predict again from it with the inputs after it,
            // counting no misprediction. The pose moves by the dropped inputs' own motion, the real divergence, at once: not eased.
            if (TakeRebase(serverTick))
            {
                Rebases++;
                LastRebaseMagnitude = error;
                if (error > MaxRebaseMagnitude) MaxRebaseMagnitude = error;
                if (LogCorrections) NebulaLog.Info($"re-base on {Identity} tick {serverTick} after dropped ticks {_droppedFrom}-{_droppedTo}: {error:0.000} m");
                h.Position = serverPos;
                h.Container = Identity.Container;
                Replay(serverTick);
                return;
            }
            // The prediction agrees with the worker, but the report brought an event for a tick already simulated without it: the
            // worker's state is right, so replay from it, now with the event on its tick, instead of waiting for the error to show.
            bool eventReplay = error <= threshold && HasUnappliedServerEvents(serverTick, _clientTick);
            // The worker moved the entity to another container since this tick was predicted (a seam, a carrier's box). The
            // prediction agrees, but the present state the client holds was carried into the new container through where the
            // containers are drawn now, not where they will be when it is simulated, and with its velocity unconverted
            // (a client converts nothing between spaces): re-predict from the worker's state in the new container instead,
            // which nothing counts as a correction (NEB-496).
            bool moved = h.Container != Identity.Container;
            if (error <= threshold && !eventReplay && !moved)
            {
                OwnSnapshotReader.Set(OwnSnapshot.ToSegment());
                ReadState(OwnSnapshotReader);
                return;
            }
            if (error <= threshold && !eventReplay)
            {
                h.Position = serverPos;
                h.Container = Identity.Container;
                Replay(serverTick);
                NoteCorrection(previousContainer, previousPosition, previousRotation);
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
            if (LogCorrections) NebulaLog.Info($"correction on {Identity} tick {serverTick} error {error:0.000} m (threshold {threshold:0.000} m at {LastCorrectionSpeed:0.#} m/s): server {serverPos:F3} predicted {predicted:F3} (recorded in {(h.Container != null ? h.Container.ContainerId : "world")}, now in {(Identity.Container != null ? Identity.Container.ContainerId : "world")})");
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
        /// the difference as a correction of metres (NEB-478). Otherwise each container is taken where it stood at
        /// <paramref name="tick"/>, the tick the position was recorded for: a moving carrier (one the pawn flew into, or out
        /// of) is drawn where it was a render delay ago, while the pawn is predicted an input lead ahead, and relating the two
        /// through where it is drawn now read its speed times that time as a correction at every crossing of its box (1.7 m
        /// at 25 m/s, NEB-496). The carrier's pose at the tick comes from its interpolation buffer, as an interpolated
        /// entity's older sample does (NEB-392); a tick the buffer has not reached goes through the current pose, as before.
        /// </summary>
        private static Vector3 Rehome(Container from, Container to, Vector3 local, uint tick)
        {
            var root = FrameRootOf(from);
            if (root != null && FrameRootOf(to) == root)
            {
                // What the frame's own container holds hangs under the root: its local position is the root's already.
                var inRoot = from.OwnPhysicsFrame ? Double3.From(local) : PhysicsFrames.PointIn(from.transform, local, root);
                return to.OwnPhysicsFrame ? inRoot.ToVector3() : PhysicsFrames.PointFrom(inRoot, to.transform, root);
            }
            var world = from != null ? DrawnWorld(from, local) : local;
            var turn = Quaternion.identity;
            if (from != null) RemoteInterpolator.CarryBack(from, tick, ref world, ref turn, 0, forPrediction: true);
            if (to != null) RemoteInterpolator.CarryForward(to, tick, ref world, ref turn, 0, forPrediction: true);
            return to != null ? DrawnLocal(to, world) : world;
        }

        /// <summary>
        /// A point local to <paramref name="c"/> where it is drawn: through its frame's render pose, composed from the local
        /// poses under the frame's root, when the container lives in a physics frame on a client (whose root may be at its
        /// simulation pose for this very reconcile, and whose cached matrices may be from either pose); through the
        /// container's own matrix otherwise.
        /// </summary>
        private static Vector3 DrawnWorld(Container c, Vector3 local)
        {
            var space = c.InnerSpace;
            var frame = space != null ? space.Frame : null;
            if (frame == null || frame.Root == null || !PhysicsFrames.RendersFrames) return c.ToWorld(local);
            if (c.OwnPhysicsFrame) return frame.LocalToRenderPrecise(Double3.From(local)).ToVector3();
            if (!PhysicsFrames.IsUnder(c.transform, frame.Root)) return c.ToWorld(local);
            return frame.LocalToRenderPrecise(PhysicsFrames.PointIn(c.transform, local, frame.Root)).ToVector3();
        }

        /// <summary>The inverse of <see cref="DrawnWorld"/>.</summary>
        private static Vector3 DrawnLocal(Container c, Vector3 world)
        {
            var space = c.InnerSpace;
            var frame = space != null ? space.Frame : null;
            if (frame == null || frame.Root == null || !PhysicsFrames.RendersFrames) return c.ToLocal(world);
            var inFrame = frame.RenderToLocalPrecise(Double3.From(world));
            if (c.OwnPhysicsFrame) return inFrame.ToVector3();
            if (!PhysicsFrames.IsUnder(c.transform, frame.Root)) return c.ToLocal(world);
            return PhysicsFrames.PointFrom(inFrame, c.transform, frame.Root);
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
            // The pose the replay starts from is the worker's at serverTick: the tick poses are rebuilt from it.
            ResetPredictedTickPose();
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
                    EndPredictedTickPose();
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
            ResetDroppedTicks();
            _hasLastServerInput = false;
            _newestServerInputTick = 0;
            ClearCorrectionOffset();
            ClearPredictedTickPose();
            ClearServerEvents();
        }
    }
}
