using Nebula;
using UnityEngine;

namespace NebulaSamples
{
    /// <summary>
    /// Flies a carrier along waypoints given in a planet's own coordinates: off the ground, out of the planet's box
    /// into the space around it, and back down. The planet is a carrier whose <see cref="Container"/> has
    /// <see cref="Container.OwnPhysicsFrame"/> and <see cref="FrameInterestMode.OwnRegions"/>; its ground is runtime
    /// containers leased under it. It is a <b>sample</b>: a scripted path that a test or a bot can repeat exactly, not
    /// a flight model.
    /// <para>
    /// Each leg goes from one waypoint to the next with a smoothstep in time, so it starts and ends at rest and peaks
    /// at <see cref="TopSpeed"/>. Each tick the authority puts the ship where the path says, in whichever space the ship
    /// is in: frame-local inside the planet's box, the space around it outside. The crossing of the box is the
    /// worker's (<c>docs/container-tree.md</c> D15), so the path never jumps when it happens; this component only keeps
    /// <c>Motion.Velocity</c> honest, which is what the worker converts at the crossing.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(Container))]
    public sealed class LapFlight : NetworkBehaviour
    {
        [Tooltip("The planet's carrier: a NetworkIdentity whose root Container has its own physics frame.")]
        public NetworkIdentity Planet;
        [Tooltip("Waypoints in the planet's own coordinates, metres. The ship starts at the first.")]
        public Vector3[] Waypoints =
        {
            new Vector3(-1000f, 0f, 0f),
            new Vector3(3000f, 300f, 0f),
            new Vector3(0f, 15000f, 0f),
            new Vector3(-970f, 0f, 0f),
        };
        [Tooltip("Peak speed of each leg, metres per second.")]
        public float TopSpeed = 300f;
        [Tooltip("Ticks to wait on the ground before the first leg.")]
        public int StartDelayTicks = 60;

        private int _ticks;

        /// <summary>Seconds since the first leg started (negative before it).</summary>
        public float FlightTime => (_ticks - StartDelayTicks) * NetworkTime.TickInterval;

        /// <summary>The path finished: the ship stands at the last waypoint.</summary>
        public bool Landed { get; private set; }

        /// <summary>Restart the lap from the first waypoint.</summary>
        public void Restart()
        {
            _ticks = 0;
            Landed = false;
        }

        public override void NetworkTick(uint tick, float deltaTime)
        {
            if (!HasAuthority || Planet == null || Waypoints == null || Waypoints.Length == 0) return;
            var planet = Planet.Carried;
            if (planet == null || planet.Frame == null) return;
            _ticks++;
            Landed = !LapPath.At(Waypoints, TopSpeed, Mathf.Max(0f, FlightTime), out var local, out var localVelocity);

            var container = Identity.Container;
            bool inFrame = container != null && container.InnerSpace == planet;
            if (inFrame)
            {
                // Frame-local, in simulation space: the frame root sits at its floating origin with no rotation.
                transform.SetPositionAndRotation(planet.Frame.LocalToSimulation(local), Quaternion.identity);
                Identity.Motion.Velocity = localVelocity;
            }
            else
            {
                var pose = Planet.transform;
                transform.SetPositionAndRotation(pose.TransformPoint(local), pose.rotation);
                var state = planet.Frame.State;
                Identity.Motion.Velocity = pose.rotation * localVelocity + (state.HasRates ? state.PointVelocity(local) : Vector3.zero);
            }
        }
    }

    /// <summary>The lap's path: legs between waypoints, each a smoothstep in time that peaks at a given speed.</summary>
    public static class LapPath
    {
        /// <summary>Seconds one leg from <paramref name="a"/> to <paramref name="b"/> takes.</summary>
        public static float LegSeconds(Vector3 a, Vector3 b, float topSpeed) => 1.5f * Vector3.Distance(a, b) / Mathf.Max(0.01f, topSpeed);

        /// <summary>Seconds the whole path takes.</summary>
        public static float Seconds(Vector3[] waypoints, float topSpeed)
        {
            float total = 0f;
            for (int i = 0; i + 1 < waypoints.Length; i++) total += LegSeconds(waypoints[i], waypoints[i + 1], topSpeed);
            return total;
        }

        /// <summary>Position and velocity <paramref name="t"/> seconds in. False once the path is done (at the last waypoint, at rest).</summary>
        public static bool At(Vector3[] waypoints, float topSpeed, float t, out Vector3 position, out Vector3 velocity)
        {
            for (int i = 0; i + 1 < waypoints.Length; i++)
            {
                Vector3 a = waypoints[i], b = waypoints[i + 1];
                float leg = LegSeconds(a, b, topSpeed);
                if (t >= leg) { t -= leg; continue; }
                float u = t / leg, s = u * u * (3f - 2f * u);
                position = Vector3.Lerp(a, b, s);
                velocity = (b - a) * (6f * u * (1f - u) / leg);
                return true;
            }
            position = waypoints[waypoints.Length - 1];
            velocity = Vector3.zero;
            return false;
        }
    }
}
