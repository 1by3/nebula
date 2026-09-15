using System;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Replicates selected position, rotation, and scale axes from the authoritative worker to clients and ghost workers.
    /// Configure change thresholds, transform space, authority, interpolation, compression, and delivery mode in the
    /// Inspector.
    /// <para>
    /// Add this component to each transform that should move on remote copies. Without it, an identity only
    /// synchronizes its initial placement and container changes. Root updates use the batched spatial stream;
    /// child updates use component state. Both honor the selected axes, thresholds, and interpolation settings.
    /// </para>
    /// <para>
    /// Nebula does not provide <c>TickSyncChildren</c> or <c>SwitchTransformSpaceWhenParented</c>. It sends every
    /// behaviour's state in the same entity update. An entity changes its parent only when it changes containers.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public class NetworkTransform : NetworkSyncBehaviour
    {
        public enum InterpolationMode : byte
        {
            /// <summary>Buffers states by tick and interpolates them at <see cref="NetworkTime.RenderTick"/>.</summary>
            Buffered = 0,
            /// <summary>Moves smoothly toward the newest received state without buffering by tick.</summary>
            SmoothDamp = 1,
        }

        /// <summary>A replicated pose. Fields without their Has* flag were not part of the update.</summary>
        public struct TransformState
        {
            public uint Tick;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public bool HasPosition, HasRotation, HasScale;
            public bool Teleport;
            /// <summary>Position/rotation are expressed in the local (parent) space rather than world space.</summary>
            public bool InLocalSpace;
            internal Container Frame;
        }

        [Header("Axes to synchronize")]
        public bool SyncPositionX = true;
        public bool SyncPositionY = true;
        public bool SyncPositionZ = true;
        public bool SyncRotAngleX = true;
        public bool SyncRotAngleY = true;
        public bool SyncRotAngleZ = true;
        public bool SyncScaleX;
        public bool SyncScaleY;
        public bool SyncScaleZ;

        [Tooltip("Include world-space motion velocity for remote extrapolation. Disable for objects that only interpolate.")]
        public bool SyncVelocity = true;
        public Vector3 Velocity { get => Identity.Motion.Velocity; set => Identity.Motion.Velocity = value; }

        [Header("Thresholds")]
        [Tooltip("Metres the position must move before an update is sent.")]
        public float PositionThreshold = 0.001f;
        [Tooltip("Degrees the rotation must change before an update is sent.")]
        public float RotAngleThreshold = 0.01f;
        public float ScaleThreshold = 0.01f;

        [Header("Space and delivery")]
        [Tooltip("Replicate localPosition/localRotation (relative to the parent) instead of world position/rotation. Scale is always local.")]
        public bool InLocalSpace;
        [Tooltip("Send ordinary updates unreliable-sequenced. Root transforms send reliable recovery within 30 ticks of movement; children send periodic keyframes. Off: reliable-ordered.")]
        public bool UseUnreliableDeltas = true;

        [Header("Precision")]
        [Tooltip("Send positions, scales and (uncompressed) rotations as 16-bit halves.")]
        public bool UseHalfFloatPrecision;
        [Tooltip("Send the full quaternion instead of the selected Euler angles (no gimbal issues; all three axes always).")]
        public bool UseQuaternionSynchronization;
        [Tooltip("With quaternion synchronization: smallest-three compression, 4 bytes per rotation.")]
        public bool UseQuaternionCompression;

        [Header("Interpolation (non-authoritative copies)")]
        public bool Interpolate = true;
        public InterpolationMode Interpolation = InterpolationMode.Buffered;
        [Tooltip("Spherically interpolate position between samples (curved paths, e.g. an orbiting object).")]
        public bool SlerpPosition;
        [Tooltip("Smooth damp only: seconds to reach the newest position.")]
        public float PositionMaxInterpolationTime = 0.1f;
        [Tooltip("Smooth damp only: seconds to reach the newest rotation.")]
        public float RotationMaxInterpolationTime = 0.1f;
        [Tooltip("Smooth damp only: seconds to reach the newest scale.")]
        public float ScaleMaxInterpolationTime = 0.1f;

        // ---- wire flags -----------------------------------------------------------------------------------

        [Flags]
        private enum Fields : byte
        {
            None = 0,
            Position = 1,
            Rotation = 2,
            Scale = 4,
            Teleport = 8,
        }

        // ---- authority-side change tracking ----------------------------------------------------------------

        private Vector3 _lastSentPosition;
        private Quaternion _lastSentRotation = Quaternion.identity;
        private Vector3 _lastSentScale = Vector3.one;
        private bool _hasLastSent;
        private Fields _pending;          // what changed since the last send (cleared in OnSyncStateSent)
        private bool _teleportPending;
        private TransformFields _lastSentFields;
        private bool _lastSentLocal;

        // ---- receiver-side state ---------------------------------------------------------------------------

        private const int Capacity = 64;
        private TransformState[] _ring;
        private uint _latestTick;
        private bool _anySample;
        private TransformState _latest;   // merged newest known state (carry-forward for deltas)
        private bool _hasLatest;
        private Vector3 _dampVelocity;
        private Vector3 _dampScaleVelocity;
        private EntityStateEntry _rootSent;
        private bool _rootHasSent, _rootRecovery;
        private uint _rootRecoveryTick;
        private TransformFields _rootReceivedFields;

        internal TransformFields RootFields
        {
            get
            {
                var f = TransformFields.None;
                if (SyncPositionX) f |= TransformFields.PositionX;
                if (SyncPositionY) f |= TransformFields.PositionY;
                if (SyncPositionZ) f |= TransformFields.PositionZ;
                if (SyncRotAngleX) f |= TransformFields.RotationX;
                if (SyncRotAngleY) f |= TransformFields.RotationY;
                if (SyncRotAngleZ) f |= TransformFields.RotationZ;
                if (SyncScaleX) f |= TransformFields.ScaleX;
                if (SyncScaleY) f |= TransformFields.ScaleY;
                if (SyncScaleZ) f |= TransformFields.ScaleZ;
                if (SyncVelocity && SyncsPosition) f |= TransformFields.Velocity;
                if (UseHalfFloatPrecision) f |= TransformFields.Half;
                if (UseQuaternionSynchronization && SyncsRotation)
                {
                    f |= TransformFields.Quaternion | TransformFields.Rotation;
                    if (UseQuaternionCompression) f |= TransformFields.Compressed;
                }
                return f;
            }
        }

        internal bool CaptureRoot(uint tick, out EntityStateEntry entry)
        {
            entry = EntityStateEntry.Snapshot(Identity);
            entry.Fields = RootFields;
            if (IsOwnerAuthoritative && _rootHasSent && entry.LocalPosition == _rootSent.LocalPosition)
                entry.Velocity = Identity.Motion.Velocity = Vector3.zero;
            var state = new TransformState
            {
                Tick = tick, Position = entry.LocalPosition, Rotation = entry.LocalRotation, Scale = entry.LocalScale,
                HasPosition = SyncsPosition, HasRotation = SyncsRotation, HasScale = SyncsScale,
                InLocalSpace = InLocalSpace, Teleport = _teleportPending,
            };
            OnAuthorityPushTransformState(ref state);
            entry.LocalPosition = state.Position; entry.LocalRotation = state.Rotation; entry.LocalScale = state.Scale;
            if (!state.HasPosition) entry.Fields &= ~(TransformFields.Position | TransformFields.Velocity);
            if (!state.HasRotation) entry.Fields &= ~TransformFields.Rotation;
            if (!state.HasScale) entry.Fields &= ~TransformFields.Scale;
            _teleportPending = state.Teleport;
            if ((entry.Fields & TransformFields.Axes) == 0 && (!_rootHasSent || entry.Fields == _rootSent.Fields)) return false;
            var p = EntityStateEntry.MergeVector(_rootSent.LocalPosition, entry.LocalPosition, entry.Fields, 0);
            var s = EntityStateEntry.MergeVector(_rootSent.LocalScale, entry.LocalScale, entry.Fields, 6);
            var r = (entry.Fields & TransformFields.Quaternion) != 0 ? entry.LocalRotation :
                Quaternion.Euler(EntityStateEntry.MergeVector(_rootSent.LocalRotation.eulerAngles, entry.LocalRotation.eulerAngles, entry.Fields, 3));
            bool changed = !_rootHasSent || entry.Fields != _rootSent.Fields || entry.Container != _rootSent.Container ||
                (p - _rootSent.LocalPosition).sqrMagnitude > PositionThreshold * PositionThreshold ||
                Quaternion.Angle(r, _rootSent.LocalRotation) > RotAngleThreshold ||
                (s - _rootSent.LocalScale).sqrMagnitude > ScaleThreshold * ScaleThreshold ||
                ((entry.Fields & TransformFields.Velocity) != 0 && (entry.Velocity - _rootSent.Velocity).sqrMagnitude > 0.000001f);
            bool recovery = _rootRecovery && tick >= _rootRecoveryTick;
            if (!changed && !recovery && !_teleportPending) return false;
            _rootSent = entry;
            _rootHasSent = true;
            if (recovery) _rootRecovery = false;
            else if (!_rootRecovery) { _rootRecovery = true; _rootRecoveryTick = tick + NetworkIdentity.SyncKeyframeInterval; }
            if (!UseUnreliableDeltas || recovery || _teleportPending || (entry.Fields & TransformFields.Axes) == 0) entry.Fields |= TransformFields.Reliable;
            if (_teleportPending) entry.Fields |= TransformFields.Teleport;
            _teleportPending = false;
            return true;
        }

        internal void ReceiveRoot(uint tick, Container container, in EntityStateEntry entry)
        {
            if (IsSyncAuthority || IsRelayingWorker || (Identity.IsLocalPlayer && Identity.Predicted != null)) return;
            if ((entry.Fields & TransformFields.Location) == 0) AcceptFields(entry.Fields);
            var buffer = Identity.Interpolator;
            if (buffer == null) buffer = Identity.Interpolator = gameObject.GetComponent<RemoteInterpolator>() ?? gameObject.AddComponent<RemoteInterpolator>();
            var p = Identity.LocalPosition;
            var r = Identity.LocalRotation;
            if (container != Identity.Container)
            {
                p = container != null ? container.ToLocal(transform.position) : transform.position;
                r = container != null ? container.InverseRotation * transform.rotation : transform.rotation;
            }
            var s = transform.localScale;
            var v = Vector3.zero;
            if (buffer.HasSamples && buffer.LatestContainer == container)
            { p = buffer.LatestLocalPosition; r = buffer.LatestLocalRotation; s = buffer.LatestScale; }
            entry.Merge(ref p, ref r, ref s, ref v);
            _rootReceivedFields = entry.Fields;
            // A container change (a ship crossing a cell seam, an entity handed to another worker) is not a jump: the
            // sample carries its container and the interpolator bridges frames, so an interpolating copy keeps its
            // buffer and glides across the seam. Only a teleport, or a copy that does not interpolate, snaps.
            bool snap = !Interpolate || (entry.Fields & TransformFields.Teleport) != 0;
            if (snap) buffer.Clear();
            buffer.Push(tick, container, p, r, v, s);
            if (snap) ApplyRoot(container, p, r, s);
            var state = new TransformState
            {
                Tick = tick, Position = !InLocalSpace && container != null ? container.ToWorld(p) : p,
                Rotation = !InLocalSpace && container != null ? container.Rotation * r : r,
                Scale = s, HasPosition = (entry.Fields & TransformFields.Position) != 0,
                HasRotation = (entry.Fields & TransformFields.Rotation) != 0, HasScale = (entry.Fields & TransformFields.Scale) != 0,
                InLocalSpace = InLocalSpace, Teleport = (entry.Fields & TransformFields.Teleport) != 0,
            };
            OnNetworkTransformStateUpdated(ref state);
        }

        private void ApplyRoot(Container container, Vector3 p, Quaternion r, Vector3 s)
        {
            if (container != Identity.Container) Identity.SetContainer(container);
            var fields = RootFields & _rootReceivedFields;
            p = EntityStateEntry.MergeVector(Identity.LocalPosition, p, fields, 0);
            if ((fields & TransformFields.Rotation) == 0) r = Identity.LocalRotation;
            else if ((fields & TransformFields.Quaternion) == 0)
                r = Quaternion.Euler(EntityStateEntry.MergeVector(Identity.LocalRotation.eulerAngles, r.eulerAngles, fields, 3));
            Identity.SetLocalPose(container, p, r);
            transform.localScale = EntityStateEntry.MergeVector(transform.localScale, s, fields, 6);
            Identity.Carried?.RefreshCache();
            Velocity = Identity.Interpolator.LatestVelocity;
        }

        private void RenderRoot(double tick)
        {
            if (!isActiveAndEnabled || IsSyncAuthority || IsRelayingWorker || (Identity.IsLocalPlayer && Identity.Predicted != null)) return;
            var buffer = Identity.Interpolator;
            if (buffer == null) return;
            buffer.SlerpPosition = SlerpPosition;
            if (!buffer.Sample(tick, out var c, out var p, out var r)) return;
            var s = buffer.SampleScale(tick);
            if (Interpolate && Interpolation == InterpolationMode.SmoothDamp)
            {
                float dt = IsServer ? NetworkTime.TickInterval : Time.deltaTime;
                p = Vector3.SmoothDamp(Identity.LocalPosition, buffer.LatestLocalPosition, ref _dampVelocity, Mathf.Max(0.0001f, PositionMaxInterpolationTime), float.MaxValue, dt);
                r = Quaternion.Slerp(Identity.LocalRotation, buffer.LatestLocalRotation, RotationMaxInterpolationTime <= 0 ? 1 : Mathf.Clamp01(dt / RotationMaxInterpolationTime));
                s = Vector3.SmoothDamp(transform.localScale, buffer.LatestScale, ref _dampScaleVelocity, Mathf.Max(0.0001f, ScaleMaxInterpolationTime), float.MaxValue, dt);
            }
            else if (!Interpolate) { c = buffer.LatestContainer; p = buffer.LatestLocalPosition; r = buffer.LatestLocalRotation; s = buffer.LatestScale; }
            buffer.SlerpPosition = SlerpPosition;
            ApplyRoot(c, p, r, s);
        }

        private void AcceptFields(TransformFields f)
        {
            SyncPositionX = (f & TransformFields.PositionX) != 0;
            SyncPositionY = (f & TransformFields.PositionY) != 0;
            SyncPositionZ = (f & TransformFields.PositionZ) != 0;
            SyncRotAngleX = (f & TransformFields.RotationX) != 0;
            SyncRotAngleY = (f & TransformFields.RotationY) != 0;
            SyncRotAngleZ = (f & TransformFields.RotationZ) != 0;
            SyncScaleX = (f & TransformFields.ScaleX) != 0;
            SyncScaleY = (f & TransformFields.ScaleY) != 0;
            SyncScaleZ = (f & TransformFields.ScaleZ) != 0;
            SyncVelocity = (f & TransformFields.Velocity) != 0;
            UseQuaternionSynchronization = (f & TransformFields.Quaternion) != 0;
            UseQuaternionCompression = (f & TransformFields.Compressed) != 0;
            UseHalfFloatPrecision = (f & TransformFields.Half) != 0;
        }

        /// <summary>This component sits on the entity root and uses the batched spatial stream.</summary>
        public bool IsRoot => Identity != null && Identity.transform == transform;

        public override Delivery SyncDelivery => UseUnreliableDeltas ? Delivery.Sequenced : Delivery.ReliableOrdered;

        private bool SyncsPosition => SyncPositionX || SyncPositionY || SyncPositionZ;
        private bool SyncsRotation => SyncRotAngleX || SyncRotAngleY || SyncRotAngleZ;
        private bool SyncsScale => SyncScaleX || SyncScaleY || SyncScaleZ;

        // ---- state hooks -----------------------------------------------------------------------------------

        /// <summary>Authority, just before a state is written. Modify it to send something other than the transform.</summary>
        protected virtual void OnAuthorityPushTransformState(ref TransformState state) { }

        /// <summary>Non-authoritative copies, after a received state was applied or buffered.</summary>
        protected virtual void OnNetworkTransformStateUpdated(ref TransformState state) { }

        // ---- public API ------------------------------------------------------------------------------------

        /// <summary>Authority only: move instantly, and make every remote copy snap instead of interpolating.</summary>
        public void Teleport(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            SetState(position, rotation, scale, true);
        }

        /// <summary>Authority only: set any of position/rotation/scale (in the configured space); null leaves a part alone.</summary>
        public void SetState(Vector3? position = null, Quaternion? rotation = null, Vector3? scale = null, bool teleport = true)
        {
            if (IsSpawned && !IsSyncAuthority)
            {
                NebulaLog.Warn($"NetworkTransform.SetState on {name} without authority; ignored");
                return;
            }
            if (position.HasValue) WritePosition(position.Value);
            if (rotation.HasValue) WriteRotation(rotation.Value);
            if (scale.HasValue) transform.localScale = scale.Value;
            if (teleport)
            {
                _teleportPending = true;
                _pending |= Fields.Position | Fields.Rotation | Fields.Scale;
                MarkSyncDirty();
            }
        }

        // ---- lifecycle -------------------------------------------------------------------------------------

        public override void OnNetworkSpawn()
        {
            _hasLastSent = false;
            _hasLatest = false;
            _rootHasSent = false;
            _rootRecovery = false;
            ClearBuffer();
        }

        public override void WriteHandoverState(NetworkWriter writer)
        {
            // Simulation state is complete even when presentation omits axes.
            writer.WriteVector3(IsRoot ? Identity.LocalPosition : transform.localPosition);
            writer.WriteQuaternion(IsRoot ? Identity.LocalRotation : transform.localRotation);
            writer.WriteVector3(transform.localScale);
        }

        public override void ReadHandoverState(NetworkReader reader)
        {
            var position = reader.ReadVector3();
            var rotation = reader.ReadQuaternion();
            if (IsRoot) Identity.SetLocalPose(Container, position, rotation);
            else { transform.localPosition = position; transform.localRotation = rotation; }
            transform.localScale = reader.ReadVector3();
            ClearBuffer();
        }

        public override void OnOriginShifted(Vector3 delta)
        {
            if (_rootSent.Container.IsNone) _rootSent.LocalPosition += delta;
            if (InLocalSpace) return;
            if (Container == null) _lastSentPosition += delta;
            if (_latest.Frame == null) _latest.Position += delta;
            if (_ring != null)
                for (int i = 0; i < _ring.Length; i++)
                    if (_ring[i].Frame == null) _ring[i].Position += delta;
        }

        public override void OnGainedAuthority()
        {
            base.OnGainedAuthority();
            _rootHasSent = false;
            _rootRecovery = false;
            _hasLastSent = false;
            ClearBuffer();
        }

        public override void OnLostAuthority()
        {
            ClearBuffer();
        }

        // ---- authority: change detection and writing -------------------------------------------------------

        protected override void AuthorityTick(uint tick, float deltaTime)
        {
            if (IsRoot && !IsOwnerAuthoritative) return; // sampled after all simulation behaviors have run
            ReadCurrent(out var pos, out var rot, out var scale);
            if (!_hasLastSent)
            {
                _pending = Fields.Position | Fields.Rotation | Fields.Scale;
                MarkSyncDirty();
                return;
            }
            var f = RootFields;
            if (f != _lastSentFields || InLocalSpace != _lastSentLocal) _pending = Fields.Position | Fields.Rotation | Fields.Scale;
            var selectedPos = EntityStateEntry.MergeVector(_lastSentPosition, pos, f, 0);
            var selectedScale = EntityStateEntry.MergeVector(_lastSentScale, scale, f, 6);
            var selectedRot = UseQuaternionSynchronization ? rot : Quaternion.Euler(EntityStateEntry.MergeVector(_lastSentRotation.eulerAngles, rot.eulerAngles, f, 3));
            if (SyncsPosition && (selectedPos - _lastSentPosition).sqrMagnitude > PositionThreshold * PositionThreshold) _pending |= Fields.Position;
            if (SyncsRotation && Quaternion.Angle(selectedRot, _lastSentRotation) > RotAngleThreshold) _pending |= Fields.Rotation;
            if (SyncsScale && (selectedScale - _lastSentScale).sqrMagnitude > ScaleThreshold * ScaleThreshold) _pending |= Fields.Scale;
            if (_pending != Fields.None) MarkSyncDirty();
        }

        public override void WriteSyncState(NetworkWriter writer, bool full)
        {
            ReadCurrent(out var pos, out var rot, out var scale);
            var state = new TransformState
            {
                Position = pos, Rotation = rot, Scale = scale, InLocalSpace = InLocalSpace, Teleport = _teleportPending,
                HasPosition = SyncsPosition && (full || (_pending & Fields.Position) != 0),
                HasRotation = SyncsRotation && (full || (_pending & Fields.Rotation) != 0),
                HasScale = SyncsScale && (full || (_pending & Fields.Scale) != 0),
            };
            OnAuthorityPushTransformState(ref state);

            var fields = Fields.None;
            if (state.HasPosition) fields |= Fields.Position;
            if (state.HasRotation) fields |= Fields.Rotation;
            if (state.HasScale) fields |= Fields.Scale;
            if (state.Teleport) fields |= Fields.Teleport;
            writer.WriteUShort((ushort)RootFields);
            writer.WriteBool(InLocalSpace);
            writer.WriteByte((byte)fields);
            if (state.HasPosition) WriteVector(writer, state.Position, SyncPositionX, SyncPositionY, SyncPositionZ);
            if (state.HasRotation) WriteRotation(writer, state.Rotation);
            if (state.HasScale) WriteVector(writer, state.Scale, SyncScaleX, SyncScaleY, SyncScaleZ);

            // Threshold comparisons are against what actually went out (not necessarily this tick's value).
            if (state.HasPosition || IsRoot) _lastSentPosition = pos;
            if (state.HasRotation || IsRoot) _lastSentRotation = rot;
            if (state.HasScale) _lastSentScale = scale;
            _hasLastSent = true;
            _lastSentFields = RootFields;
            _lastSentLocal = InLocalSpace;
        }

        protected internal override void OnSyncStateSent()
        {
            _pending = Fields.None;
            _teleportPending = false;
        }

        // ---- non-authoritative copies: reading and presenting ----------------------------------------------

        public override void ReadSyncState(NetworkReader reader, uint tick, bool full)
        {
            if (tick != 0 && _hasLatest && tick < _latest.Tick) return;
            var wireFields = (TransformFields)reader.ReadUShort();
            bool local = reader.ReadBool();
            if (!IsSyncAuthority) { AcceptFields(wireFields); InLocalSpace = local; }
            var fields = (Fields)reader.ReadByte();
            var state = _hasLatest ? _latest : new TransformState { Rotation = Quaternion.identity, Scale = Vector3.one };
            state.Tick = tick;
            state.InLocalSpace = InLocalSpace;
            state.Teleport = (fields & Fields.Teleport) != 0;
            state.HasPosition = (fields & Fields.Position) != 0;
            state.HasRotation = (fields & Fields.Rotation) != 0;
            state.HasScale = (fields & Fields.Scale) != 0;
            // Unsynchronised axes of a partially synced vector are filled from the newest known value, expressed in
            // wire space (world-space values are container-local on the wire, like the identity stream).
            var c = InLocalSpace || Identity == null ? null : Identity.SyncContainer;
            Vector3 fbPos, fbScale; Quaternion fbRot;
            if (_hasLatest)
            {
                var projected = Project(_latest);
                fbPos = c != null ? c.ToLocal(projected.Position) : projected.Position;
                fbRot = c != null ? c.InverseRotation * projected.Rotation : projected.Rotation;
                fbScale = _latest.Scale;
            }
            else
            {
                ReadStored(out fbPos, out fbRot, out fbScale);
                if (c != null) { fbPos = c.ToLocal(fbPos); fbRot = c.InverseRotation * fbRot; }
            }
            if (state.HasPosition) state.Position = ReadVector(reader, fbPos, SyncPositionX, SyncPositionY, SyncPositionZ);
            if (state.HasRotation) state.Rotation = ReadRotation(reader, fbRot);
            if (state.HasScale) state.Scale = ReadVector(reader, fbScale, SyncScaleX, SyncScaleY, SyncScaleZ);

            // The chunk is consumed. Our own echo (owner authority: the worker re-broadcasts what we sent) stops here.
            if (IsSyncAuthority) return;

            // Retain the reference frame while buffering, so moving carriers do not leave child samples behind.
            state.Frame = c;
            if (!state.HasPosition) state.Position = fbPos;
            if (!state.HasRotation) state.Rotation = fbRot;
            // Carry-forward: a delta only updates the fields it carries; the rest keep the newest known value.
            var previous = _latest;
            bool hadPrevious = _hasLatest;
            _latest = state;
            _hasLatest = true;

            bool snap = state.Teleport || !Interpolate || tick == 0 || IsRelayingWorker;
            if (snap)
            {
                ClearBuffer();
                _hasLatest = true;
                _latest = state;
                var projected = Project(state);
                Apply(projected.Position, projected.Rotation, state.Scale, state.HasPosition, state.HasRotation, state.HasScale);
                if (state.Teleport && IsRoot && Identity.Interpolator != null)
                {
                    // The identity stream will resume from the new place; do not lerp across the jump.
                    Identity.Interpolator.Clear();
                    if (state.HasPosition || state.HasRotation)
                        Identity.Interpolator.Push(tick, Container, Identity.LocalPosition, Identity.LocalRotation, Identity.Motion.Velocity);
                }
                if (IsRelayingWorker && IsRoot && state.HasPosition && !state.Teleport)
                {
                    // Owner-driven root: derive a velocity for the identity stream so remote copies can extrapolate.
                    var v = hadPrevious ? (Project(state).Position - Project(previous).Position) / (Math.Max(1u, tick - previous.Tick) * NetworkTime.TickInterval) : Vector3.zero;
                    Identity.Motion.Velocity = Vector3.ClampMagnitude(v, 100f);
                }
            }
            else
            {
                Push(_latest);
            }
            OnNetworkTransformStateUpdated(ref state);
        }

        protected override void ApplyOwnerState(NetworkReader reader, bool full)
        {
            // The worker: snap to what the owner sent (ReadSyncState snaps for IsRelayingWorker) and remember it as
            // the last-sent baseline so WriteSyncState re-broadcasts exactly these fields.
            ReadSyncState(reader, NetworkTime.Tick, full);
            if (_latest.HasPosition) _pending |= Fields.Position;
            if (_latest.HasRotation) _pending |= Fields.Rotation;
            if (_latest.HasScale) _pending |= Fields.Scale;
            if (_latest.Teleport) _teleportPending = true;
        }

        public override void RemoteTick(double renderTick)
        {
            if (IsRoot) { RenderRoot(renderTick); return; }
            if (IsSyncAuthority || IsRelayingWorker || !Interpolate || !_hasLatest) return;
            if (Interpolation == InterpolationMode.SmoothDamp)
            {
                SmoothDampTowardsLatest();
                return;
            }
            if (!_anySample) return;
            SampleBuffered(renderTick, out var pos, out var rot, out var scale, out bool hasPos, out bool hasRot, out bool hasScale);
            Apply(pos, rot, scale, hasPos, hasRot, hasScale);
        }

        // ---- buffer ----------------------------------------------------------------------------------------

        private void ClearBuffer()
        {
            if (_ring != null) for (int i = 0; i < Capacity; i++) _ring[i].Tick = 0;
            _anySample = false;
            _dampVelocity = Vector3.zero;
            _dampScaleVelocity = Vector3.zero;
        }

        private void Push(in TransformState merged)
        {
            if (_ring == null) _ring = new TransformState[Capacity];
            if (_anySample && merged.Tick + Capacity <= _latestTick) return; // too old to matter
            if (!_anySample || merged.Tick > _latestTick) _latestTick = merged.Tick;
            _anySample = true;
            // Store the merged (carry-forward) state so every sample is complete; flags say what is known at all.
            var s = merged;
            s.HasPosition = true; s.HasRotation = true; s.HasScale = true;
            _ring[merged.Tick % Capacity] = s;
        }

        private bool TryGet(uint tick, out TransformState s)
        {
            s = Project(_ring[tick % Capacity]);
            return s.Tick == tick && tick != 0;
        }

        private static TransformState Project(TransformState s)
        {
            if (s.Frame != null) { s.Position = s.Frame.ToWorld(s.Position); s.Rotation = s.Frame.Rotation * s.Rotation; }
            return s;
        }

        private void SampleBuffered(double renderTick, out Vector3 pos, out Quaternion rot, out Vector3 scale, out bool hasPos, out bool hasRot, out bool hasScale)
        {
            hasPos = hasRot = hasScale = true;
            if (renderTick >= _latestTick)
            {
                // Nothing newer yet: hold the newest sample (no velocity to extrapolate with).
                var l = Project(_ring[_latestTick % Capacity]);
                pos = l.Position; rot = l.Rotation; scale = l.Scale;
                return;
            }
            uint floor = (uint)Math.Floor(renderTick);
            TransformState before = default, after = default;
            bool hasBefore = false, hasAfter = false;
            for (int i = 0; i < Capacity; i++)
            {
                uint t = floor - (uint)i;
                if (t > floor) break; // underflow
                if (TryGet(t, out before)) { hasBefore = true; break; }
            }
            for (uint t = floor + 1; t <= _latestTick; t++)
            {
                if (TryGet(t, out after)) { hasAfter = true; break; }
            }
            if (hasBefore && hasAfter)
            {
                float span = after.Tick - before.Tick;
                float f = span > 0 ? (float)((renderTick - before.Tick) / span) : 0f;
                pos = SlerpPosition ? Vector3.Slerp(before.Position, after.Position, f) : Vector3.Lerp(before.Position, after.Position, f);
                rot = Quaternion.Slerp(before.Rotation, after.Rotation, f);
                scale = Vector3.Lerp(before.Scale, after.Scale, f);
                return;
            }
            var only = hasAfter ? after : before;
            pos = only.Position; rot = only.Rotation; scale = only.Scale;
        }

        private void SmoothDampTowardsLatest()
        {
            var latest = Project(_latest);
            ReadStored(out var pos, out var rot, out var scale);
            float dt = IsServer ? NetworkTime.TickInterval : Time.deltaTime;
            if (SyncsPosition) pos = Vector3.SmoothDamp(pos, latest.Position, ref _dampVelocity, Mathf.Max(0.0001f, PositionMaxInterpolationTime), float.MaxValue, dt);
            if (SyncsRotation)
            {
                float t = RotationMaxInterpolationTime <= 0f ? 1f : Mathf.Clamp01(dt / RotationMaxInterpolationTime);
                rot = Quaternion.Slerp(rot, latest.Rotation, t);
            }
            if (SyncsScale) scale = Vector3.SmoothDamp(scale, _latest.Scale, ref _dampScaleVelocity, Mathf.Max(0.0001f, ScaleMaxInterpolationTime), float.MaxValue, dt);
            Apply(pos, rot, scale, SyncsPosition, SyncsRotation, SyncsScale);
        }

        // ---- transform access in the configured space --------------------------------------------------------

        /// <summary>Current pose in wire space: local, or container-local for world-space sync (like the identity stream).</summary>
        private void ReadCurrent(out Vector3 pos, out Quaternion rot, out Vector3 scale)
        {
            scale = transform.localScale;
            if (InLocalSpace)
            {
                pos = transform.localPosition;
                rot = transform.localRotation;
                return;
            }
            var c = Container;
            if (c != null)
            {
                pos = c.ToLocal(transform.position);
                rot = c.InverseRotation * transform.rotation;
            }
            else
            {
                pos = transform.position;
                rot = transform.rotation;
            }
        }

        /// <summary>Current pose in stored (receiver) space: local, or absolute world.</summary>
        private void ReadStored(out Vector3 pos, out Quaternion rot, out Vector3 scale)
        {
            scale = transform.localScale;
            if (InLocalSpace) { pos = transform.localPosition; rot = transform.localRotation; }
            else { pos = transform.position; rot = transform.rotation; }
        }

        private void WritePosition(Vector3 p)
        {
            if (InLocalSpace) transform.localPosition = p;
            else transform.position = p;
        }

        private void WriteRotation(Quaternion r)
        {
            if (InLocalSpace) transform.localRotation = r;
            else transform.rotation = r;
        }

        /// <summary>Apply stored-space values to the transform, touching only the synchronised axes.</summary>
        private void Apply(Vector3 pos, Quaternion rot, Vector3 scale, bool hasPos, bool hasRot, bool hasScale)
        {
            var frame = InLocalSpace ? null : Container;
            if (hasPos && SyncsPosition)
            {
                ReadStored(out var cur, out _, out _);
                if (frame != null) { cur = frame.ToLocal(cur); pos = frame.ToLocal(pos); }
                var p = new Vector3(SyncPositionX ? pos.x : cur.x, SyncPositionY ? pos.y : cur.y, SyncPositionZ ? pos.z : cur.z);
                if (InLocalSpace) transform.localPosition = p; else transform.position = frame != null ? frame.ToWorld(p) : p;
            }
            if (hasRot && SyncsRotation)
            {
                Quaternion r = frame != null ? frame.InverseRotation * rot : rot;
                if (!UseQuaternionSynchronization && !(SyncRotAngleX && SyncRotAngleY && SyncRotAngleZ))
                {
                    ReadStored(out _, out var curRot, out _);
                    if (frame != null) curRot = frame.InverseRotation * curRot;
                    var cur = curRot.eulerAngles;
                    var e = r.eulerAngles;
                    r = Quaternion.Euler(SyncRotAngleX ? e.x : cur.x, SyncRotAngleY ? e.y : cur.y, SyncRotAngleZ ? e.z : cur.z);
                }
                if (InLocalSpace) transform.localRotation = r; else transform.rotation = frame != null ? frame.Rotation * r : r;
            }
            if (hasScale && SyncsScale)
            {
                var cur = transform.localScale;
                transform.localScale = new Vector3(SyncScaleX ? scale.x : cur.x, SyncScaleY ? scale.y : cur.y, SyncScaleZ ? scale.z : cur.z);
            }
        }

        // ---- wire encoding ---------------------------------------------------------------------------------

        private void WriteFloat(NetworkWriter w, float v)
        {
            if (UseHalfFloatPrecision) w.WriteHalf(v); else w.WriteFloat(v);
        }

        private float ReadFloat(NetworkReader r) => UseHalfFloatPrecision ? r.ReadHalf() : r.ReadFloat();

        private void WriteVector(NetworkWriter w, Vector3 v, bool x, bool y, bool z)
        {
            if (x) WriteFloat(w, v.x);
            if (y) WriteFloat(w, v.y);
            if (z) WriteFloat(w, v.z);
        }

        private Vector3 ReadVector(NetworkReader r, Vector3 fallback, bool x, bool y, bool z)
        {
            var v = fallback;
            if (x) v.x = ReadFloat(r);
            if (y) v.y = ReadFloat(r);
            if (z) v.z = ReadFloat(r);
            return v;
        }

        private void WriteRotation(NetworkWriter w, Quaternion q)
        {
            if (UseQuaternionSynchronization)
            {
                if (UseQuaternionCompression) w.WriteCompressedQuaternion(q);
                else if (UseHalfFloatPrecision) { w.WriteHalf(q.x); w.WriteHalf(q.y); w.WriteHalf(q.z); w.WriteHalf(q.w); }
                else w.WriteQuaternion(q);
                return;
            }
            WriteVector(w, q.eulerAngles, SyncRotAngleX, SyncRotAngleY, SyncRotAngleZ);
        }

        private Quaternion ReadRotation(NetworkReader r, Quaternion fallback)
        {
            if (UseQuaternionSynchronization)
            {
                if (UseQuaternionCompression) return r.ReadCompressedQuaternion();
                if (UseHalfFloatPrecision) return new Quaternion(r.ReadHalf(), r.ReadHalf(), r.ReadHalf(), r.ReadHalf()).normalized;
                return r.ReadQuaternion();
            }
            return Quaternion.Euler(ReadVector(r, fallback.eulerAngles, SyncRotAngleX, SyncRotAngleY, SyncRotAngleZ));
        }
    }
}
