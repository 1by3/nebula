using Nebula;
using UnityEngine;

namespace NebulaSamples
{
    /// <summary>
    /// A character no player owns that walks to a point, including a point on a moving platform, and steps on and off
    /// that platform as a player's pawn does. It is a <b>sample</b>, not part of Nebula: how a character decides where
    /// to go, how it avoids obstacles and how it animates are game decisions. Copy it into your game and change it.
    /// <para>
    /// There is nothing to do for the crossing itself. The worker that simulates the walker resolves its container
    /// every tick, whoever owns it. When the walker's origin enters a container that has its own physics frame (the
    /// platform's box), the worker moves it into that frame and converts its position, rotation and
    /// <see cref="NetworkMotionState.Velocity"/> at the frame's pose for that tick; when it leaves by more than
    /// <see cref="NebulaConfig.HandoverHysteresis"/>, it converts them back. What this component does is the part a
    /// game always owns: it works in whatever space it is in (<see cref="NetworkIdentity.FromScope"/> turns a target
    /// in the scope's own space into that space), it writes what it did into <c>Motion.Velocity</c> each tick, and it
    /// reads its fall speed back from there when the space changes.
    /// </para>
    /// <para>
    /// Spawn it with <see cref="NebulaWorker.SpawnServerDriven"/> and call <see cref="WalkTo(Transform)"/> on the
    /// worker that has authority. Clients need no code: they interpolate it like any other entity, across the
    /// crossing too.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class ScopeWalker : NetworkBehaviour
    {
        [Tooltip("Walking speed, metres per second.")]
        public float Speed = 3f;
        [Tooltip("Gravity along the walker's own down, metres per second squared. Inside a physics frame that is the frame's down.")]
        public float Gravity = 9.81f;
        [Tooltip("How close to its target the walker stops, metres (measured along the ground).")]
        public float ArriveDistance = 0.1f;

        private Transform _target;
        private Vector3 _point;
        private bool _hasGoal;
        private float _fall;
        private CharacterController _controller;

        /// <summary>The walker has somewhere to go.</summary>
        public bool HasGoal => _hasGoal;

        /// <summary>The walker is within <see cref="ArriveDistance"/> of its target, along the ground.</summary>
        public bool Arrived { get; private set; }

        /// <summary>
        /// Authority: walk to <paramref name="target"/> and keep following it. A child of a moving carrier (a marker on
        /// a platform's deck) moves with the carrier, so the walker boards it and then stands on that spot.
        /// </summary>
        public void WalkTo(Transform target)
        {
            _target = target;
            _hasGoal = target != null;
            Arrived = false;
        }

        /// <summary>Authority: walk to a fixed point given in the scope's own space.</summary>
        public void WalkTo(Vector3 scopePoint)
        {
            _target = null;
            _point = scopePoint;
            _hasGoal = true;
            Arrived = false;
        }

        /// <summary>Authority: stop where the walker is.</summary>
        public void Stop()
        {
            _target = null;
            _hasGoal = false;
        }

        /// <summary>The current target in the scope's own space.</summary>
        public Vector3 TargetInScope => _target != null ? InScope(_target) : _point;

        /// <summary>
        /// A transform's position in the scope's own space. A worker simulates each physics frame at its own origin, so
        /// a transform under an entity inside a frame reads frame coordinates; the entity it belongs to converts it out.
        /// A carrier's own transform, and anything in the scope's scene, already reads the scope's space.
        /// </summary>
        public static Vector3 InScope(Transform t)
        {
            var owner = t.GetComponentInParent<NetworkIdentity>();
            return owner != null ? owner.ToScope(t.position) : t.position;
        }

        public override void OnNetworkSpawn() => _controller = GetComponent<CharacterController>();

        public override void NetworkTick(uint tick, float deltaTime)
        {
            if (!HasAuthority || deltaTime <= 0f) return;
            if (_controller == null) _controller = GetComponent<CharacterController>();
            var here = transform.position;
            var walk = Vector3.zero;
            if (_hasGoal)
            {
                // The target, in the space the walker simulates in this tick: the scope's, or a platform's frame.
                var to = Identity.FromScope(TargetInScope) - here;
                to.y = 0f;
                float distance = to.magnitude;
                Arrived = distance <= ArriveDistance;
                if (distance > 0.001f) walk = to / distance * Mathf.Min(Speed, distance / deltaTime);
            }
            _fall = _controller.isGrounded ? -1f : _fall - Gravity * deltaTime;
            _controller.Move((walk + Vector3.up * _fall) * deltaTime);
            // What the walker did, in its own space. Nebula converts this when the walker changes space, and a client
            // uses it to extrapolate between updates.
            Identity.Motion.Velocity = (transform.position - here) / deltaTime;
        }

        public override void OnContainerChanged(Container previous, Container current)
        {
            // Into or out of a platform's frame: Nebula converted Motion.Velocity into the new space, the platform's
            // motion included, so a fall in progress continues along the new space's down.
            var from = previous != null ? previous.InnerSpace : null;
            var to = current != null ? current.InnerSpace : null;
            if (HasAuthority && from != to) _fall = Mathf.Min(0f, Identity.Motion.Velocity.y);
        }
    }
}
