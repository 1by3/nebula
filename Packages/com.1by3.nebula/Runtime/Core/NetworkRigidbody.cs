using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Puts a Unity <see cref="Rigidbody"/> under the mesh's authority model. On the worker that owns the entity the
    /// body is dynamic and PhysX simulates it; everywhere else (ghosts on neighbouring workers, every client) it is
    /// kinematic and follows the owner's state stream. Linear velocity is mirrored into
    /// <see cref="NetworkIdentity.Velocity"/> every tick so remote copies extrapolate and a handover keeps momentum;
    /// angular velocity only matters to the next simulating worker, so it travels with the handover alone through
    /// <see cref="NetworkBehaviour.WriteHandoverState"/>. Add it next to the NetworkIdentity of any physics prop.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public sealed class NetworkRigidbody : NetworkBehaviour
    {
        private Rigidbody _body;
        private Vector3 _handoverAngularVelocity;

        public Rigidbody Body => _body != null ? _body : (_body = GetComponent<Rigidbody>());

        public override void OnNetworkSpawn()
        {
            Body.isKinematic = !HasAuthority;
            if (HasAuthority) Body.linearVelocity = Identity.Velocity;
        }

        public override void OnGainedAuthority()
        {
            Body.isKinematic = false;
            Body.linearVelocity = Identity.Velocity;
            Body.angularVelocity = _handoverAngularVelocity;
            _handoverAngularVelocity = Vector3.zero;
        }

        public override void OnLostAuthority()
        {
            Body.isKinematic = true;
        }

        public override void NetworkTick(uint tick, float deltaTime)
        {
            Identity.Velocity = Body.linearVelocity;
        }

        public override void WriteHandoverState(NetworkWriter writer)
        {
            writer.WriteVector3(Body.angularVelocity);
        }

        public override void ReadHandoverState(NetworkReader reader)
        {
            _handoverAngularVelocity = reader.ReadVector3();
        }

        /// <summary>Authority only: set the body's momentum (e.g. to launch a freshly spawned prop).</summary>
        public void SetVelocity(Vector3 linear, Vector3 angular = default)
        {
            Identity.Velocity = linear;
            if (Body.isKinematic) { _handoverAngularVelocity = angular; return; }
            Body.linearVelocity = linear;
            Body.angularVelocity = angular;
        }
    }
}
