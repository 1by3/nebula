using System;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Replicates a Transform the way NGO's NetworkTransform does: per-axis position/rotation/scale selection,
    /// change thresholds, local or world space, server or owner authority, buffered tick interpolation or smooth
    /// damping on the receiving side, teleport, half-float and smallest-three quaternion compression, and reliable
    /// deltas or unreliable deltas healed by keyframes.
    /// <para>
    /// The entity root is a special case in Nebula: its position, rotation and velocity already ride the identity's
    /// world-state stream at full rate to every ghost and client, so a NetworkTransform on the root does not send
    /// them again. On the root it adds scale, <see cref="Teleport"/> (which snaps the identity interpolator too) and
    /// owner authority (the owner's pose travels to the worker here, then to everyone through the identity stream).
    /// On any child transform (turret, door, held item) it replicates everything selected.
    /// </para>
    /// <para>
    /// NGO options with no counterpart here: <c>TickSyncChildren</c> (every behaviour's chunk already rides the same
    /// per-entity per-tick message) and <c>SwitchTransformSpaceWhenParented</c> (an entity's parent only changes when
    /// its container changes, and world-space values are carried in container space on the wire and resolved with the
    /// container index of the same tick).
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public class NetworkTransform : NetworkSyncBehaviour
    {
        public enum InterpolationMode : byte
        {
            /// <summary>Tick-buffered interpolation at <see cref="NetworkTime.RenderTick"/> (NGO's Lerp / Legacy lerp): exact, a few ticks behind.</summary>
            Buffered = 0,
            /// <summary>Smooth-damp towards the newest state (NGO's Smooth dampening): never behind, never exact.</summary>
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
        }

        [Header("Axes to synchronize")]
        public bool SyncPositionX = true;
        public bool SyncPositionY = true;
        public bool SyncPositionZ = true;
        public bool SyncRotAngleX = true;
        public bool SyncRotAngleY = true;
        public bool SyncRotAngleZ = true;
        public bool SyncScaleX = true;
        public bool SyncScaleY = true;
        public bool SyncScaleZ = true;

        [Header("Thresholds")]
        [Tooltip("Metres the position must move before an update is sent.")]
        public float PositionThreshold = 0.001f;
        [Tooltip("Degrees the rotation must change before an update is sent.")]
        public float RotAngleThreshold = 0.01f;
        public float ScaleThreshold = 0.01f;

        [Header("Space and delivery")]
        [Tooltip("Replicate localPosition/localRotation (relative to the parent) instead of world position/rotation. Scale is always local.")]
        public bool InLocalSpace;
        [Tooltip("Send deltas unreliable-sequenced with a keyframe every NetworkIdentity.SyncKeyframeInterval ticks, instead of reliable-ordered.")]
        public bool UseUnreliableDeltas;

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

        // ---- receiver-side state ---------------------------------------------------------------------------

        private const int Capacity = 64;
        private TransformState[] _ring;
        private uint _latestTick;
        private bool _anySample;
        private TransformState _latest;   // merged newest known state (carry-forward for deltas)
        private bool _hasLatest;
        private Vector3 _dampVelocity;
        private Vector3 _dampScaleVelocity;

        /// <summary>This component sits on the entity root, whose position/rotation the identity stream already carries.</summary>
        public bool IsRoot => Identity != null && Identity.transform == transform;

        public override Delivery SyncDelivery => UseUnreliableDeltas ? Delivery.Sequenced : Delivery.ReliableOrdered;

        private bool SyncsPosition => SyncPositionX || SyncPositionY || SyncPositionZ;
        private bool SyncsRotation => SyncRotAngleX || SyncRotAngleY || SyncRotAngleZ;
        private bool SyncsScale => SyncScaleX || SyncScaleY || SyncScaleZ;

        /// <summary>
        /// The root's position/rotation travel in the identity stream, except from an owner to its worker (the worker
        /// has no other way to learn them) and for a teleport (receivers must snap their identity interpolator).
        /// </summary>
        private bool PoseGoesInChunk => !IsRoot || _teleportPending || (IsOwnerAuthoritative && IsOwner);

        // ---- hooks (NGO names) -----------------------------------------------------------------------------

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
            ClearBuffer();
        }

        public override void OnGainedAuthority()
        {
            base.OnGainedAuthority();
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
            ReadCurrent(out var pos, out var rot, out var scale);
            if (!_hasLastSent)
            {
                _pending = Fields.Position | Fields.Rotation | Fields.Scale;
                MarkSyncDirty();
                return;
            }
            if (SyncsPosition && (pos - _lastSentPosition).sqrMagnitude >= PositionThreshold * PositionThreshold) _pending |= Fields.Position;
            if (SyncsRotation && Quaternion.Angle(rot, _lastSentRotation) >= RotAngleThreshold) _pending |= Fields.Rotation;
            if (SyncsScale && (scale - _lastSentScale).sqrMagnitude >= ScaleThreshold * ScaleThreshold) _pending |= Fields.Scale;
            if (_pending != Fields.None) MarkSyncDirty();
        }

        public override void WriteSyncState(NetworkWriter writer, bool full)
        {
            ReadCurrent(out var pos, out var rot, out var scale);
            var state = new TransformState
            {
                Position = pos, Rotation = rot, Scale = scale, InLocalSpace = InLocalSpace, Teleport = _teleportPending,
                HasPosition = SyncsPosition && (full || (_pending & Fields.Position) != 0) && PoseGoesInChunk,
                HasRotation = SyncsRotation && (full || (_pending & Fields.Rotation) != 0) && PoseGoesInChunk,
                HasScale = SyncsScale && (full || (_pending & Fields.Scale) != 0),
            };
            OnAuthorityPushTransformState(ref state);

            var fields = Fields.None;
            if (state.HasPosition) fields |= Fields.Position;
            if (state.HasRotation) fields |= Fields.Rotation;
            if (state.HasScale) fields |= Fields.Scale;
            if (state.Teleport) fields |= Fields.Teleport;
            writer.WriteByte((byte)fields);
            if (state.HasPosition) WriteVector(writer, state.Position, SyncPositionX, SyncPositionY, SyncPositionZ);
            if (state.HasRotation) WriteRotation(writer, state.Rotation);
            if (state.HasScale) WriteVector(writer, state.Scale, SyncScaleX, SyncScaleY, SyncScaleZ);

            // Threshold comparisons are against what actually went out (not necessarily this tick's value).
            if (state.HasPosition || IsRoot) _lastSentPosition = pos;
            if (state.HasRotation || IsRoot) _lastSentRotation = rot;
            if (state.HasScale) _lastSentScale = scale;
            _hasLastSent = true;
        }

        protected internal override void OnSyncStateSent()
        {
            _pending = Fields.None;
            _teleportPending = false;
        }

        // ---- non-authoritative copies: reading and presenting ----------------------------------------------

        public override void ReadSyncState(NetworkReader reader, uint tick, bool full)
        {
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
                fbPos = c != null ? c.ToLocal(_latest.Position) : _latest.Position;
                fbRot = c != null ? c.InverseRotation * _latest.Rotation : _latest.Rotation;
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

            // Wire space -> stored space (absolute world, or local).
            if (c != null)
            {
                if (state.HasPosition) state.Position = c.ToWorld(state.Position);
                if (state.HasRotation) state.Rotation = c.Rotation * state.Rotation;
            }
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
                Apply(state.Position, state.Rotation, state.Scale, state.HasPosition, state.HasRotation, state.HasScale);
                if (state.Teleport && IsRoot && Identity.Interpolator != null)
                {
                    // The identity stream will resume from the new place; do not lerp across the jump.
                    Identity.Interpolator.Clear();
                    if (state.HasPosition || state.HasRotation)
                        Identity.Interpolator.Push(tick, Container, Identity.LocalPosition, Identity.LocalRotation, Identity.Velocity);
                }
                if (IsRelayingWorker && IsRoot && state.HasPosition && !state.Teleport)
                {
                    // Owner-driven root: derive a velocity for the identity stream so remote copies can extrapolate.
                    var v = hadPrevious ? (state.Position - previous.Position) * NetworkTime.TickRate : Vector3.zero;
                    Identity.Velocity = Vector3.ClampMagnitude(v, 100f);
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
            s = _ring[tick % Capacity];
            return s.Tick == tick && tick != 0;
        }

        private void SampleBuffered(double renderTick, out Vector3 pos, out Quaternion rot, out Vector3 scale, out bool hasPos, out bool hasRot, out bool hasScale)
        {
            hasPos = hasRot = hasScale = true;
            if (renderTick >= _latestTick)
            {
                // Nothing newer yet: hold the newest sample (no velocity to extrapolate with).
                var l = _ring[_latestTick % Capacity];
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
            ReadStored(out var pos, out var rot, out var scale);
            float dt = IsServer ? NetworkTime.TickInterval : Time.deltaTime;
            if (SyncsPosition) pos = Vector3.SmoothDamp(pos, _latest.Position, ref _dampVelocity, Mathf.Max(0.0001f, PositionMaxInterpolationTime), float.MaxValue, dt);
            if (SyncsRotation)
            {
                float t = RotationMaxInterpolationTime <= 0f ? 1f : Mathf.Clamp01(dt / RotationMaxInterpolationTime);
                rot = Quaternion.Slerp(rot, _latest.Rotation, t);
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
            else transform.position = Container != null ? Container.ToWorld(p) : p;
        }

        private void WriteRotation(Quaternion r)
        {
            if (InLocalSpace) transform.localRotation = r;
            else transform.rotation = Container != null ? Container.Rotation * r : r;
        }

        /// <summary>Apply stored-space values to the transform, touching only the synchronised axes.</summary>
        private void Apply(Vector3 pos, Quaternion rot, Vector3 scale, bool hasPos, bool hasRot, bool hasScale)
        {
            // Root pose (position/rotation) belongs to the identity stream unless the worker is relaying an owner
            // or this is a teleport; both come through with the flags set, so simply honour the flags.
            if (hasPos && SyncsPosition)
            {
                ReadStored(out var cur, out _, out _);
                var p = new Vector3(SyncPositionX ? pos.x : cur.x, SyncPositionY ? pos.y : cur.y, SyncPositionZ ? pos.z : cur.z);
                if (InLocalSpace) transform.localPosition = p; else transform.position = p;
            }
            if (hasRot && SyncsRotation)
            {
                Quaternion r = rot;
                if (!UseQuaternionSynchronization && !(SyncRotAngleX && SyncRotAngleY && SyncRotAngleZ))
                {
                    ReadStored(out _, out var curRot, out _);
                    var cur = curRot.eulerAngles;
                    var e = rot.eulerAngles;
                    r = Quaternion.Euler(SyncRotAngleX ? e.x : cur.x, SyncRotAngleY ? e.y : cur.y, SyncRotAngleZ ? e.z : cur.z);
                }
                if (InLocalSpace) transform.localRotation = r; else transform.rotation = r;
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
