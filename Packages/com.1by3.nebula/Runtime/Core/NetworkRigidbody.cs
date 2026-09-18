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

        public Rigidbody Body => _body != null ? _body : (_body = GetComponent<Rigidbody>());

        public override void OnNetworkSpawn()
        {
            _handoverKinematic = Body.isKinematic;
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
            writer.WriteBool(Body.isKinematic);
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
