using Nebula;
using UnityEngine;

namespace NebulaSamples
{
    /// <summary>
    /// A reference lag-compensated hitscan validator, in the smallest form that is still honest. It is a
    /// <b>sample</b>, not part of Nebula: rewinding colliders, deciding what a shot may claim and applying damage are
    /// game decisions (see <c>docs/state-history.md</c> §6). Copy it into your game and change all of it.
    /// <para>
    /// The shape: the shooter's client sends the tick it was rendering with its shot. The worker that has authority
    /// over the shooter validates that tick against the window it can answer for, then asks every candidate victim
    /// what it looked like then (<see cref="NetworkIdentity.StateAt"/>, answered on the authority and on any worker
    /// holding a ghost), and tests the ray against a capsule placed at that recorded pose. No collider is moved.
    /// </para>
    /// </summary>
    public sealed class LagCompensatedHitscan : NetworkBehaviour
    {
        [Tooltip("Total height and radius of the capsule the sample uses for a victim's hitboxes.")]
        public float VictimHeight = 1.8f;
        public float VictimRadius = 0.35f;
        [Tooltip("Metres the shot may reach.")]
        public float Range = 120f;
        [Tooltip("How far into the past a claimed tick may point, on top of what the window can answer.")]
        public int MaxClaimTicks = 24;

        /// <summary>
        /// The worker this behaviour runs on. A real game hands it over once from
        /// <see cref="NebulaGameMode.OnWorkerStarted"/> and keeps it; the sample finds it so it stands alone.
        /// </summary>
        public NebulaWorker Worker;

        private void Awake()
        {
            if (Worker == null) Worker = FindAnyObjectByType<NebulaWorker>();
        }

        /// <summary>
        /// Called by the shooter's client. <paramref name="aimTick"/> is the tick the client was rendering, which is
        /// <c>NetworkTime.RenderTick</c>.
        /// </summary>
        public void Fire(Vector3 origin, Vector3 direction, uint aimTick) =>
            ServerRpc(RpcFire, origin, direction, aimTick);

        [ServerRpc]
        private void RpcFire(Vector3 origin, Vector3 direction, uint aimTick)
        {
            // Your game mode already has the worker (OnWorkerStarted); this sample keeps it simple.
            var worker = Worker;
            if (worker == null) return;

            // 1. Is the claim one we could possibly check? Nebula never decides this; a window that cannot answer
            //    and a tick further back than the game allows are different refusals, and both are the game's.
            uint now = worker.CurrentTick;
            if (aimTick > now || now - aimTick > (uint)MaxClaimTicks) return;

            // 2. Rewind the candidates. Anything this worker holds can answer: the entities it simulates and the
            //    ghosts of its neighbours' entities standing near the seam.
            var ray = new Ray(origin, direction.normalized);
            NetworkIdentity hit = null;
            float hitDistance = float.MaxValue;
            foreach (var candidate in worker.Entities)
            {
                if (candidate == null || candidate == Identity) continue;
                if (!candidate.TryGetStateAt(aimTick, out var state)) continue; // outside the window: no claim
                if ((state.Container != null ? state.Container.InstanceId : 0UL) != Identity.InstanceId) continue;

                var bottom = state.Position + Vector3.up * VictimRadius;
                var top = state.Position + Vector3.up * (VictimHeight - VictimRadius);
                if (!IntersectsCapsule(ray, bottom, top, VictimRadius, out float distance)) continue;
                if (distance > Range || distance >= hitDistance) continue;
                hit = candidate;
                hitDistance = distance;
            }
            if (hit == null) return;

            // 3. Apply the result on whoever has authority over the victim. Locally when that is us, through the
            //    cross-worker call contract when the victim is a ghost here (docs/cross-worker-calls.md): the call
            //    is fenced by the victim's epoch and applied at most once, even if it hands over in flight.
            var health = hit.GetComponent<SampleHealth>();
            if (health != null) health.ApplyDamage(10);
        }

        /// <summary>Ray against a capsule, closest hit. Analytic on purpose: no physics query, so nothing is moved.</summary>
        private static bool IntersectsCapsule(Ray ray, Vector3 a, Vector3 b, float radius, out float distance)
        {
            distance = 0f;
            if (radius < 0f || ray.direction.sqrMagnitude < 1e-12f) return false;
            var direction = ray.direction.normalized;
            var ab = b - a;
            var ao = ray.origin - a;
            float abab = Vector3.Dot(ab, ab);
            float abd = Vector3.Dot(ab, direction);
            float abao = Vector3.Dot(ab, ao);
            var closest = a + ab * (abab > 1e-12f ? Mathf.Clamp01(abao / abab) : 0f);
            if ((ray.origin - closest).sqrMagnitude <= radius * radius) return true;

            // A finite cylinder plus its two endpoint spheres is exactly the capsule. Test all three and keep
            // the closest forward intersection, including rays parallel to the axis and zero-length capsules.
            float best = Mathf.Min(SphereDistance(ray.origin, direction, a, radius),
                SphereDistance(ray.origin, direction, b, radius));
            if (abab > 1e-12f)
            {
                var q = direction - ab * (abd / abab);
                var r = ao - ab * (abao / abab);
                float qq = Vector3.Dot(q, q);
                float qr = Vector3.Dot(q, r);
                float rr = Vector3.Dot(r, r) - radius * radius;
                float disc = qr * qr - qq * rr;
                if (qq > 1e-12f && disc >= 0f)
                {
                    float root = Mathf.Sqrt(disc);
                    ConsiderSide((-qr - root) / qq);
                    ConsiderSide((-qr + root) / qq);
                }
            }
            if (float.IsPositiveInfinity(best)) return false;
            distance = best;
            return true;

            void ConsiderSide(float t)
            {
                float axial = abao + t * abd;
                if (t >= 0f && t < best && axial >= 0f && axial <= abab) best = t;
            }
        }

        private static float SphereDistance(Vector3 origin, Vector3 direction, Vector3 center, float radius)
        {
            var offset = origin - center;
            float projection = Vector3.Dot(offset, direction);
            float disc = projection * projection - (offset.sqrMagnitude - radius * radius);
            if (disc < 0f) return float.PositiveInfinity;
            float root = Mathf.Sqrt(disc);
            float near = -projection - root;
            if (near >= 0f) return near;
            float far = -projection + root;
            return far >= 0f ? far : float.PositiveInfinity;
        }
    }

    /// <summary>The victim side of the sample: a health pool with a stance in the recorded history.</summary>
    public sealed class SampleHealth : NetworkBehaviour
    {
        public NetworkVariable<int> Health = new NetworkVariable<int>(100);

        /// <summary>
        /// Marked, so a validator can ask what stance the victim was in at the claimed tick - a crouching player is
        /// a shorter capsule. Only marked variables are snapshotted; see <c>docs/state-history.md</c> §5.
        /// </summary>
        [SyncHistory] public NetworkVariable<byte> Stance = new NetworkVariable<byte>();

        /// <summary>
        /// Runs on whoever has authority over this entity. Called in-process when the shooter's worker is also the
        /// victim's, and forwarded over the worker link (fenced, at most once) when it is not.
        /// </summary>
        public void ApplyDamage(int amount) => AuthorityRpc(RpcApplyDamage, amount);

        [AuthorityRpc]
        private void RpcApplyDamage(int amount)
        {
            if (!HasAuthority) return;
            Health.Value = Mathf.Max(0, Health.Value - amount);
        }
    }
}
