using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Puts a Unity <see cref="Rigidbody"/> under the mesh's authority model. On the worker that owns the entity the
    /// body is dynamic and PhysX simulates it; everywhere else (ghosts on neighbouring workers, every client) it is
    /// kinematic and follows the owner's state stream. Linear velocity is mirrored into
    /// <see cref="NetworkIdentity.Motion"/> every tick so remote copies extrapolate and a handover keeps momentum;
    /// angular velocity only matters to the next simulating worker, so it travels with the handover alone through
    /// <see cref="NetworkBehaviour.WriteHandoverState"/>. Add it next to the NetworkIdentity of any physics prop.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(NetworkTransform))]
    [DisallowMultipleComponent]
    public sealed class NetworkRigidbody : NetworkBehaviour
    {
        private Rigidbody _body;
        private Vector3 _handoverAngularVelocity;
        private bool _handoverKinematic, _handoverSleeping;
        private bool _simulate = true, _held, _gated, _gatedKinematic;

        public Rigidbody Body => _body != null ? _body : (_body = GetComponent<Rigidbody>());

        /// <summary>
        /// The game's say in whether the authoritative body is handed to the solver: clear it while the ground under
        /// the prop is not streamed in, or for a prop that is not physical right now. Local to this process and not
        /// carried by a handover - each owner decides for itself. The body's own kinematic flag is kept underneath
        /// and comes back when the gate opens.
        /// </summary>
        public bool Simulate
        {
            get => _simulate;
            set
            {
                if (_simulate == value) return;
                bool was = Forced;
                _simulate = value;
                Reforce(was);
            }
        }

        /// <summary>
        /// A second reason to keep the authoritative body kinematic, next to <see cref="Simulate"/>: the entity is
        /// fixed to its container (<see cref="FrameAttachment"/>, <c>docs/frame-bodies.md</c> D9). Set it before the
        /// entity spawns or gains authority (while reading persistent or handover state) and the body is never
        /// dynamic for a tick. Like <see cref="Simulate"/>, the body's own kinematic flag is kept underneath, and the
        /// handover carries that flag, not this one.
        /// </summary>
        internal bool Held
        {
            get => _held;
            set
            {
                if (_held == value) return;
                bool was = Forced;
                _held = value;
                Reforce(was);
            }
        }

        /// <summary>The body is held kinematic on the authority whatever its own flag says.</summary>
        private bool Forced => !_simulate || _held;

        /// <summary>
        /// A reason to hold the body came or went. On the authority: hold it the moment the first reason appears, and
        /// hand it back to the solver, from the pose the transform has now, when the last one clears. Elsewhere the
        /// body is kinematic anyway and the next gain of authority reads <see cref="Forced"/>.
        /// </summary>
        private void Reforce(bool wasForced)
        {
            if (!HasAuthority) return;
            bool forced = Forced;
            if (forced == wasForced) return;
            if (forced) { Gate(Body.isKinematic); return; }
            if (!_gated) return;
            _gated = false;
            if (_gatedKinematic) return;
            SyncBodyPose();
            Body.isKinematic = false;
        }

        /// <summary>
        /// Hold the body kinematic and remember the flag underneath. Authority can land before or after the spawn
        /// callback, so only the first call records it: the second would read back the flag this one forced.
        /// </summary>
        private void Gate(bool underlying)
        {
            if (!_gated) { _gated = true; _gatedKinematic = underlying; }
            Body.isKinematic = true;
        }

        public override void OnNetworkSpawn()
        {
            _handoverKinematic = Body.isKinematic;
            if (HasAuthority && Forced) { Gate(Body.isKinematic); return; }
            if (HasAuthority) SyncBodyPose();
            Body.isKinematic = !HasAuthority;
            if (HasAuthority) Body.linearVelocity = Identity.Motion.Velocity;
        }

        /// <summary>
        /// PhysX keeps its own copy of the pose. One written through the transform while the body was kinematic (a
        /// restore, a handover, the ghost stream) has not reached it until the next transform sync, and a body that
        /// turns dynamic before that simulates from the stale pose - the prefab's spawn point - and drags the
        /// transform back to it. Push the pose across before the body is handed to the solver.
        /// </summary>
        private void SyncBodyPose()
        {
            Body.position = transform.position;
            Body.rotation = transform.rotation;
        }

        public override void OnGainedAuthority()
        {
            if (Forced) { Gate(_handoverKinematic); return; }
            if (!_handoverKinematic) SyncBodyPose();
            Body.isKinematic = _handoverKinematic;
            if (Body.isKinematic) return;
            Body.linearVelocity = Identity.Motion.Velocity;
            Body.angularVelocity = _handoverAngularVelocity;
            _handoverAngularVelocity = Vector3.zero;
            if (_handoverSleeping) Body.Sleep();
        }

        public override void OnLostAuthority()
        {
            _gated = false;
            Body.isKinematic = true;
        }

        public override void NetworkTick(uint tick, float deltaTime)
        {
            Identity.Motion.Velocity = Body.linearVelocity;
        }

        public override void WriteHandoverState(NetworkWriter writer)
        {
            writer.WriteVector3(Body.linearVelocity);
            writer.WriteVector3(Body.angularVelocity);
            writer.WriteBool(_gated ? _gatedKinematic : Body.isKinematic);
            writer.WriteBool(Body.IsSleeping());
        }

        public override void ReadHandoverState(NetworkReader reader)
        {
            Identity.Motion.Velocity = reader.ReadVector3();
            _handoverAngularVelocity = reader.ReadVector3();
            _handoverKinematic = reader.ReadBool();
            _handoverSleeping = reader.ReadBool();
        }

        /// <summary>Authority only: set the body's momentum (e.g. to launch a freshly spawned prop).</summary>
        public void SetVelocity(Vector3 linear, Vector3 angular = default)
        {
            Identity.Motion.Velocity = linear;
            if (Body.isKinematic) { _handoverAngularVelocity = angular; return; }
            Body.linearVelocity = linear;
            Body.angularVelocity = angular;
        }
    }
}
