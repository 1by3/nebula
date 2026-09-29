using Nebula;
using UnityEngine;

namespace NebulaSamples
{
    /// <summary>
    /// A server-owned wanderer: walks to a random point inside its area, waits there a few seconds, and picks the next
    /// one. It is a <b>sample</b>, not part of Nebula: what a crowd member does is the game's. What it shows is what
    /// any server-owned walker needs to cross worker seams: everything <see cref="NetworkTick"/> reads is carried in
    /// <see cref="WriteHandoverState"/>, because the worker that receives it has only what the stream and the handover
    /// gave it (<c>docs/server-owned-entities.md</c> §3).
    /// <para>
    /// It moves by the <c>deltaTime</c> it is given, so it walks at the same speed whatever the entity's
    /// <see cref="NetworkIdentity.UpdateInterval"/>: an entity updated every 60 ticks is given a second at a time.
    /// </para>
    /// </summary>
    public sealed class CrowdWalker : NetworkBehaviour
    {
        [Tooltip("Walking speed in metres per second.")]
        public float Speed = 1.4f;
        [Tooltip("Seconds it waits at each point, chosen between these.")]
        public float MinWait = 1f;
        public float MaxWait = 6f;
        [Tooltip("The box it wanders in, in world space: its centre and its size on the ground (x, z).")]
        public Vector3 AreaCentre;
        public Vector2 AreaSize = new Vector2(100f, 100f);

        private Vector3 _target;
        private float _wait;
        private uint _random;

        /// <summary>Where it is walking to now.</summary>
        public Vector3 Target => _target;
        /// <summary>Seconds it will still wait before it walks on; 0 while walking.</summary>
        public float Wait => _wait;

        public override void OnGainedAuthority()
        {
            // A handover has already set everything (state first, then this hook); only a fresh spawn starts here.
            if (_random != 0) return;
            _random = (uint)(Identity.NetId * 2654435761UL) | 1u;
            _target = NextTarget();
        }

        public override void NetworkTick(uint tick, float deltaTime)
        {
            if (_wait > 0f)
            {
                _wait -= deltaTime;
                Identity.Motion.Velocity = Vector3.zero;
                return;
            }
            var position = transform.position;
            var toTarget = _target - position;
            toTarget.y = 0f;
            float step = Speed * deltaTime;
            if (toTarget.magnitude <= step)
            {
                transform.position = new Vector3(_target.x, position.y, _target.z);
                Identity.Motion.Velocity = Vector3.zero;
                _wait = Mathf.Lerp(MinWait, MaxWait, Next01());
                _target = NextTarget();
                return;
            }
            var direction = toTarget.normalized;
            transform.SetPositionAndRotation(position + direction * step, Quaternion.LookRotation(direction));
            Identity.Motion.Velocity = direction * Speed;
        }

        public override void WriteHandoverState(NetworkWriter writer)
        {
            writer.WriteVector3(_target);
            writer.WriteFloat(_wait);
            writer.WriteUInt(_random);
            writer.WriteVector3(AreaCentre);
            writer.WriteFloat(AreaSize.x);
            writer.WriteFloat(AreaSize.y);
        }

        public override void ReadHandoverState(NetworkReader reader)
        {
            _target = reader.ReadVector3();
            _wait = reader.ReadFloat();
            _random = reader.ReadUInt();
            AreaCentre = reader.ReadVector3();
            AreaSize = new Vector2(reader.ReadFloat(), reader.ReadFloat());
        }

        private Vector3 NextTarget()
        {
            float x = AreaCentre.x + (Next01() - 0.5f) * AreaSize.x;
            float z = AreaCentre.z + (Next01() - 0.5f) * AreaSize.y;
            return new Vector3(x, AreaCentre.y, z);
        }

        // xorshift32: deterministic and carried across a handover, so the next worker picks the same next points.
        private float Next01()
        {
            uint x = _random == 0 ? 1u : _random;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _random = x;
            return (x & 0xFFFFFF) / (float)0x1000000;
        }
    }
}
