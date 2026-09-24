using System;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Fixes an entity to a container at a pose in the container's space: a crate strapped to a cargo grid in a ship's
    /// hold, a turret bolted to a deck, a crate set on a shelf in a building. An attached entity does not slide, is not
    /// moved into a neighboring container, keeps its attachment through a handover, a restore from the store and a late
    /// joiner's spawn, and is let go gently. Add it to any entity, with or without a <see cref="NetworkRigidbody"/>.
    /// <para>
    /// The authority calls <see cref="Attach(Container, Vector3, Quaternion, bool)"/>, at a pose or, with no arguments,
    /// where the entity is now, and <see cref="Detach"/>; any worker holding a copy can ask with
    /// <see cref="RequestAttach(Container, Vector3, Quaternion, bool)"/> and <see cref="RequestDetach"/>. It works in a
    /// ship's physics frame, in a fixed container (a building, a chunk) and in a carrier without a frame. While attached:
    /// </para>
    /// <list type="bullet">
    /// <item>the entity sits in the container it is attached to, and the worker's tick does not re-resolve its container
    /// from its position. It is still handed to the worker that owns the container, so an entity attached to a
    /// container another worker owns moves there on the next tick, and a re-deal of the container takes it along;</item>
    /// <item>its body, if it has one, is kinematic on the authority, and its pose is put back at the attached pose every
    /// tick, so other bodies can rest on it;</item>
    /// <item>when its container changes for any other reason (its carrier despawned and set it down, it was placed
    /// somewhere else), it detaches.</item>
    /// </list>
    /// <para>
    /// A detach keeps the entity in its container, where it is. Its body turns dynamic with the velocity the attached
    /// pose had over the last tick (zero in a physics frame or a fixed container, the carrier's velocity in a carrier
    /// without a frame), and for <see cref="DetachSettleTicks"/> the body's depenetration speed is capped at
    /// <see cref="DetachDepenetrationVelocity"/>, so a crate attached slightly inside another is eased apart. The
    /// attachment is saved with a <see cref="PersistentEntity"/> and comes back attached, including as cargo stowed with
    /// <see cref="CargoPolicy.Stow"/>. See <c>docs/frame-bodies.md</c> §2.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FrameAttachment : NetworkBehaviour
    {
        [Tooltip("For this many ticks after a detach the body's depenetration speed is capped at DetachDepenetrationVelocity, so an overlap is eased apart instead of launching the body.")]
        public int DetachSettleTicks = 10;
        [Tooltip("The body's depenetration speed, in m/s, for DetachSettleTicks ticks after a detach.")]
        public float DetachDepenetrationVelocity = 1f;

        private NetworkVariable<bool> _attached = new NetworkVariable<bool>();

        /// <summary>
        /// Version of the <see cref="WritePersistentState"/> chunk. Version 1 was written only while attached and held
        /// the pose alone; version 2 always holds the flag, and the pose after it when attached.
        /// </summary>
        private const byte PersistVersion = 2;

        private Rigidbody _body;
        private NetworkRigidbody _networkBody;
        private bool _subscribed, _reported, _placing, _hasPose;
        private bool _bareHeld, _bareWasKinematic;
        private Vector3 _simPosition, _previousSimPosition;
        private int _simSamples;
        private int _settleTicks;
        private float _savedDepenetration;

        /// <summary>The entity is attached to its container. The same on every process that holds it.</summary>
        public bool Attached => _attached.Value;

        /// <summary>The container the entity is attached to (always the container it is in), or null when it is not attached.</summary>
        public Container AttachedTo => _attached.Value && Identity != null ? Identity.Container : null;

        /// <summary>The attached pose's position in the container's space. Known on the authority; meaningless when not attached.</summary>
        public Vector3 AttachedLocalPosition { get; private set; }

        /// <summary>The attached pose's rotation in the container's space. Known on the authority; meaningless when not attached.</summary>
        public Quaternion AttachedLocalRotation { get; private set; } = Quaternion.identity;

        /// <summary>
        /// The entity was attached (true) or detached (false). Raised on every process that holds the entity: on the
        /// authority when it happens, on ghosts and clients when they hear of it, and when an attached entity spawns
        /// on a process, a late joiner included.
        /// </summary>
        public event Action<bool> AttachedChanged;

        private Rigidbody Body => _body != null ? _body : (_body = GetComponent<Rigidbody>());
        private NetworkRigidbody NetworkBody => _networkBody != null ? _networkBody : (_networkBody = GetComponent<NetworkRigidbody>());

        private void Awake() => Subscribe();

        private void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            _attached.OnValueChanged += (_, attached) => Report(attached);
        }

        private void Report(bool attached)
        {
            if (_reported == attached) return;
            _reported = attached;
            try { AttachedChanged?.Invoke(attached); }
            catch (Exception e) { NebulaLog.Error($"FrameAttachment.AttachedChanged on {name} threw: {e}"); }
        }

        // ------------------------------------------------------------------------------------ authority API

        /// <summary>
        /// Authority: attach the entity to <paramref name="container"/> at a pose in the container's space, moving it
        /// into the container first. Pass <paramref name="teleport"/> to make remote copies snap to the pose instead of
        /// interpolating there. Attaching an attached entity moves the attachment. Returns false, with a warning, when
        /// this process is not the entity's authority or the container cannot hold the entity (one it carries itself).
        /// </summary>
        public bool Attach(Container container, Vector3 localPosition, Quaternion localRotation, bool teleport = false)
        {
            if (!MayAct(nameof(Attach))) return false;
            if (container == null)
            {
                NebulaLog.Warn($"FrameAttachment.Attach on {Identity}: no container given; ignored");
                return false;
            }
            var identity = Identity;
            if (identity.Container != container)
            {
                _placing = true;
                try { identity.SetContainer(container); }
                finally { _placing = false; }
                if (identity.Container != container)
                {
                    NebulaLog.Warn($"FrameAttachment.Attach on {identity}: it cannot be placed in {container.ContainerId}; not attached");
                    return false;
                }
            }
            AttachedLocalPosition = localPosition;
            AttachedLocalRotation = localRotation;
            _hasPose = true;
            RestoreDepenetration();
            Hold(true);
            identity.SetLocalPose(container, localPosition, localRotation);
            SyncBody();
            identity.Motion.Velocity = Vector3.zero;
            if (teleport && identity.RootTransform != null) identity.RootTransform.SetState(teleport: true);
            _simSamples = 0;
            RecordSimPosition();
            _attached.Value = true;
            MarkPersistDirty();
            return true;
        }

        /// <summary>Authority: attach the entity to the container it is in, where it is now.</summary>
        public bool Attach()
        {
            if (!MayAct(nameof(Attach))) return false;
            var identity = Identity;
            if (identity.Container == null)
            {
                NebulaLog.Warn($"FrameAttachment.Attach on {identity}: it is in no container; not attached");
                return false;
            }
            return Attach(identity.Container, identity.LocalPosition, identity.LocalRotation);
        }

        /// <summary>
        /// Authority: let the entity go, where it is, in the container it is in. Returns false when it was not attached
        /// or this process is not its authority.
        /// </summary>
        public bool Detach()
        {
            if (!MayAct(nameof(Detach))) return false;
            if (!_attached.Value) return false;
            Release();
            return true;
        }

        /// <summary>
        /// From any worker that simulates or ghosts the entity: ask its authority to
        /// <see cref="Attach(Container, Vector3, Quaternion, bool)"/>. The container is named by its id, so it must be
        /// known on the authority's worker too.
        /// </summary>
        public void RequestAttach(Container container, Vector3 localPosition, Quaternion localRotation, bool teleport = false)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            AuthorityRpc<string, Vector3, Quaternion, bool>(AttachRpc, container.ContainerId, localPosition, localRotation, teleport);
        }

        /// <summary>From any worker that simulates or ghosts the entity: ask its authority to <see cref="Attach()"/> it where it is.</summary>
        public void RequestAttach() => AuthorityRpc(AttachHereRpc);

        /// <summary>From any worker that simulates or ghosts the entity: ask its authority to <see cref="Detach"/> it.</summary>
        public void RequestDetach() => AuthorityRpc(DetachRpc);

        [AuthorityRpc]
        private void AttachRpc(string containerId, Vector3 localPosition, Quaternion localRotation, bool teleport)
        {
            var container = ContainerRegistry.FindById(containerId);
            if (container == null)
            {
                NebulaLog.Warn($"FrameAttachment.RequestAttach on {Identity}: container {containerId} is not known here; not attached");
                return;
            }
            Attach(container, localPosition, localRotation, teleport);
        }

        [AuthorityRpc]
        private void AttachHereRpc() => Attach();

        [AuthorityRpc]
        private void DetachRpc() => Detach();

        private bool MayAct(string what)
        {
            if (HasAuthority && IsSpawned) return true;
            NebulaLog.Warn($"FrameAttachment.{what} on {name}: only the entity's authority can do this (use Request{what} from another worker); ignored");
            return false;
        }

        // ------------------------------------------------------------------------------------ holding and letting go

        /// <summary>Pin the entity's container and hold its body, or let both go (<c>docs/frame-bodies.md</c> D8, D9).</summary>
        private void Hold(bool on)
        {
            var identity = Identity;
            if (identity != null) identity.ContainerPinned = on && identity.HasAuthority;
            var networkBody = NetworkBody;
            if (networkBody != null)
            {
                networkBody.Held = on;
                return;
            }
            // A bare Rigidbody: the worker makes it dynamic on its authority, so the hold is applied here directly.
            var body = Body;
            if (body == null || identity == null || !identity.HasAuthority) return;
            if (on)
            {
                if (!_bareHeld) { _bareHeld = true; _bareWasKinematic = body.isKinematic; }
                body.isKinematic = true;
            }
            else if (_bareHeld)
            {
                _bareHeld = false;
                if (_bareWasKinematic) return;
                SyncBody();
                body.isKinematic = false;
            }
        }

        /// <summary>Push the transform's pose to PhysX, which keeps its own copy until the next transform sync.</summary>
        private void SyncBody()
        {
            var body = Body;
            if (body == null) return;
            body.position = transform.position;
            body.rotation = transform.rotation;
        }

        /// <summary>Detach: unpin, release the body with the attached pose's last velocity, and ease any overlap apart (D11).</summary>
        private void Release()
        {
            var velocity = _simSamples >= 2 ? (_simPosition - _previousSimPosition) / NetworkTime.TickInterval : Vector3.zero;
            _attached.Value = false;
            Hold(false);
            _simSamples = 0;
            var identity = Identity;
            if (identity != null) identity.Motion.Velocity = velocity;
            var body = Body;
            if (body != null && !body.isKinematic)
            {
                body.linearVelocity = velocity;
                body.angularVelocity = Vector3.zero;
                if (DetachSettleTicks > 0)
                {
                    if (_settleTicks == 0) _savedDepenetration = body.maxDepenetrationVelocity;
                    body.maxDepenetrationVelocity = Mathf.Min(_savedDepenetration, Mathf.Max(0f, DetachDepenetrationVelocity));
                    _settleTicks = DetachSettleTicks;
                }
            }
            MarkPersistDirty();
        }

        private void RestoreDepenetration()
        {
            if (_settleTicks == 0) return;
            _settleTicks = 0;
            var body = Body;
            if (body != null) body.maxDepenetrationVelocity = _savedDepenetration;
        }

        /// <summary>Where the attached pose put the entity this tick, in simulation space, for the velocity a detach leaves it with.</summary>
        private void RecordSimPosition()
        {
            _previousSimPosition = _simPosition;
            _simPosition = transform.position;
            _simSamples++;
        }

        // ------------------------------------------------------------------------------------ lifecycle

        public override void NetworkTick(uint tick, float deltaTime)
        {
            if (_attached.Value)
            {
                var container = Identity.Container;
                if (container == null) { Release(); return; }
                // Every tick, so nothing that touched the transform can make it drift (D10).
                Identity.SetLocalPose(container, AttachedLocalPosition, AttachedLocalRotation);
                RecordSimPosition();
                return;
            }
            if (_settleTicks > 0 && --_settleTicks == 0)
            {
                var body = Body;
                if (body != null) body.maxDepenetrationVelocity = _savedDepenetration;
            }
        }

        public override void OnNetworkSpawn()
        {
            Subscribe();
            if (HasAuthority && _attached.Value)
            {
                // Restored attached: the state was read before the spawn, so the body was never dynamic.
                if (!_hasPose) TakeCurrentPose();
                Hold(true);
                if (Identity.Container != null) Identity.SetLocalPose(Identity.Container, AttachedLocalPosition, AttachedLocalRotation);
                _simSamples = 0;
                RecordSimPosition();
            }
            if (_attached.Value) Report(true);
        }

        public override void OnNetworkDespawn()
        {
            RestoreDepenetration();
            _reported = false;
            // A scene entity keeps its values and its body when it leaves the network: the attachment must not outlive
            // this life, so one spawned again with no record is not attached, pinned or held (D14). The record was
            // written before the despawn. The body gets its own kinematic flag back, as the scene authored it, before
            // the next spawn reads it. Reset after _reported, so no listener hears a detach.
            Hold(false);
            if (HasAuthority) _attached.Value = false;
            _hasPose = false;
            _simSamples = 0;
        }

        public override void OnGainedAuthority()
        {
            if (!_attached.Value) return;
            // A handover wrote the pose (ReadHandoverState); anything else keeps the entity where it is.
            if (!_hasPose) TakeCurrentPose();
            Hold(true);
            _simSamples = 0;
            RecordSimPosition();
        }

        public override void OnLostAuthority()
        {
            RestoreDepenetration();
            if (Identity != null) Identity.ContainerPinned = false;
            if (NetworkBody != null) NetworkBody.Held = false;
            _bareHeld = false;
            _hasPose = false;
            _simSamples = 0;
        }

        public override void OnContainerChanged(Container previous, Container current)
        {
            // Moved out of its container by anything but Attach: it no longer sits where it was attached (D13).
            if (_placing || !_attached.Value || !HasAuthority || !IsSpawned) return;
            Release();
        }

        public override void OnOriginShifted(Vector3 delta)
        {
            _simPosition += delta;
            _previousSimPosition += delta;
        }

        private void TakeCurrentPose()
        {
            AttachedLocalPosition = Identity.LocalPosition;
            AttachedLocalRotation = Identity.LocalRotation;
            _hasPose = true;
        }

        // ------------------------------------------------------------------------------------ handover and persistence

        public override void WriteHandoverState(NetworkWriter writer)
        {
            writer.WriteBool(_attached.Value);
            if (!_attached.Value) return;
            writer.WriteVector3(AttachedLocalPosition);
            writer.WriteQuaternion(AttachedLocalRotation);
        }

        public override void ReadHandoverState(NetworkReader reader)
        {
            bool attached = reader.ReadBool();
            if (attached)
            {
                AttachedLocalPosition = reader.ReadVector3();
                AttachedLocalRotation = reader.ReadQuaternion();
                _hasPose = true;
            }
            // Before authority lands: the body is held from the first moment it is this worker's (D12).
            _attached.Value = attached;
            if (NetworkBody != null) NetworkBody.Held = attached;
            if (Identity != null) Identity.ContainerPinned = false; // set with authority, in OnGainedAuthority
        }

        /// <summary>
        /// Always saved, attached or not, so the record is the one source of truth: a version byte, the flag, and the
        /// attached pose when attached (<c>docs/frame-bodies.md</c> D14).
        /// </summary>
        public override void WritePersistentState(NetworkWriter writer)
        {
            writer.WriteByte(PersistVersion);
            writer.WriteBool(_attached.Value);
            if (!_attached.Value) return;
            writer.WriteVector3(AttachedLocalPosition);
            writer.WriteQuaternion(AttachedLocalRotation);
        }

        public override void ReadPersistentState(NetworkReader reader)
        {
            byte version = reader.ReadByte();
            bool attached = false;
            if (version == 1) attached = true; // written only while attached
            else if (version == PersistVersion) attached = reader.ReadBool();
            else NebulaLog.Warn($"FrameAttachment on {name}: saved state version {version} is not one this build reads; restored unattached");
            _hasPose = attached;
            if (attached)
            {
                AttachedLocalPosition = reader.ReadVector3();
                AttachedLocalRotation = reader.ReadQuaternion();
            }
            // Before the spawn: the body is held from its first tick.
            _attached.Value = attached;
            if (NetworkBody != null) NetworkBody.Held = attached;
        }
    }
}
