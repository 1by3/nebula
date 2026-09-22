using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Nebula.World;
using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// A client-side watchdog for interest management and content streaming: once a second it writes one
    /// machine-parsable line saying what this client can currently see and whether any of it is wrong. Meant for
    /// headless bot clients in a soak run — `nebula logs bot1 | grep nebula-probe` is then a time series you can
    /// assert on — but it costs nothing to leave on a player client while tuning.
    /// <para>
    /// It lives in the package rather than in a game because the four things worth failing a soak on are
    /// framework-level, not game-level: replicas leaking past the exit radius (hysteresis or unsubscribe is
    /// broken), a net id spawned twice without a despawn in between (a re-entry racing a late despawn), a replica
    /// standing in a chunk this process has not loaded (entity traffic overtook container ownership), and a
    /// replica count that grows with world size instead of staying bounded.
    /// </para>
    /// <para>
    /// The line is <c>key=value</c> pairs after a fixed <c>[nebula-probe]</c> tag, so a log parser can split on
    /// spaces and never has to track the field order:
    /// <code>
    /// [nebula-probe] t=12.0 pos=64.2,0.0,-31.5 cell=1,0,-1 replicas=37 near=12 dmin=3.4 dmax=131.8
    ///                beyond=0 dup=0 chunks=25 orphans=0 spawns=41 despawns=4
    /// </code>
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InterestProbe : MonoBehaviour
    {
        /// <summary>Seconds between lines. One per second is small enough to leave on for hours and dense enough to see a leak appear.</summary>
        public float IntervalSeconds = 1f;

        /// <summary>
        /// Metres past <c>InterestRadius + InterestExitMargin</c> a replica may sit before it is counted in
        /// <c>beyond</c>. Not zero: a replica is measured against a pose that is one interpolation delay old and
        /// the gateway evaluates at <c>InterestEvalHz</c>, so a few metres of overshoot is correct behaviour and
        /// only a persistent count is a bug.
        /// </summary>
        public float SlackMeters = 24f;

        /// <summary>
        /// Seconds of the client's own motion added to <see cref="SlackMeters"/>. An entity may only leave a set
        /// after it has been beyond the exit radius for <c>InterestLingerSeconds</c>, the gateway re-evaluates at
        /// <c>InterestEvalHz</c>, and the pose measured here is an interpolated one a few ticks old. A walking
        /// player covers a metre or two in that window and the flat slack absorbs it, but a client in a ship at
        /// 60 m/s leaves everything it holds tens of metres behind entirely correctly. Without this the probe
        /// reports a leak every time anyone flies.
        /// </summary>
        public float SlackSeconds = 1.6f;

        /// <summary>Also write the line through <see cref="NebulaLog"/> at info level (on by default; a bot's log is the point).</summary>
        public bool Log = true;

        /// <summary>The most recent line, for a debug overlay or a test that would rather read than parse a log.</summary>
        public string LastLine { get; private set; } = "";

        // Counters since the probe started; a soak asserts on the deltas.
        public int Duplicates { get; private set; }
        public int Spawns { get; private set; }
        public int Despawns { get; private set; }
        public int Orphans { get; private set; }
        public int Beyond { get; private set; }
        /// <summary>
        /// Replicas riding in a dynamic container whose carrier this client does not hold. A
        /// carrier and everything it carries enter and leave a client's set as one unit, so a passenger without
        /// its ship is the observable failure of that rule: the client would render a seated player hanging in
        /// the air where the ship used to be, and the pose it receives is relative to a frame it cannot resolve.
        /// </summary>
        public int Carrierless { get; private set; }

        private NebulaClient _client;
        private readonly HashSet<ulong> _live = new HashSet<ulong>();
        /// <summary>The net ids resident this pass, so the carrier test is a lookup rather than a second scan.</summary>
        private readonly HashSet<ulong> _held = new HashSet<ulong>();
        private readonly StringBuilder _line = new StringBuilder(192);
        private readonly StringBuilder _offenders = new StringBuilder(128);
        private readonly StringBuilder _orphanNames = new StringBuilder(128);
        private float _next;
        private float _exitRadius = float.PositiveInfinity;
        private Vector3 _lastOrigin;
        private Vector3Int _lastOriginCell;
        private float _lastOriginAt;
        private bool _hadOrigin;

        /// <summary>Metres per second above which a sample-to-sample jump is a teleport or an origin shift, not motion.</summary>
        private const float MaxPlausibleSpeed = 400f;

        /// <summary>Attach a probe to the object holding <paramref name="client"/> (idempotent).</summary>
        public static InterestProbe Attach(NebulaClient client)
        {
            if (client == null) return null;
            var probe = client.GetComponent<InterestProbe>();
            if (probe == null) probe = client.gameObject.AddComponent<InterestProbe>();
            return probe;
        }

        private void OnEnable()
        {
            var boot = NebulaBootstrap.Instance;
            _client = GetComponent<NebulaClient>() ?? (boot != null ? boot.Client : null);
            if (_client == null) { enabled = false; return; }
            var config = NebulaRuntime.Config ?? (boot != null ? boot.Config : null);
            if (config != null) _exitRadius = config.ToInterestSettings().ExitRadius;
            _client.EntitySpawned += OnSpawned;
            _client.EntityDespawned += OnDespawned;
            foreach (var e in _client.Entities) if (e != null) _live.Add(e.NetId);
        }

        private void OnDisable()
        {
            if (_client == null) return;
            _client.EntitySpawned -= OnSpawned;
            _client.EntityDespawned -= OnDespawned;
        }

        private void OnSpawned(NetworkIdentity e)
        {
            if (e == null) return;
            Spawns++;
            // A spawn for an id already in the set means the gateway never despawned the previous replica: the
            // client now holds two views of one entity, and only one of them will ever be updated again.
            if (!_live.Add(e.NetId)) { Duplicates++; NebulaLog.Warn($"[nebula-probe] duplicate spawn for netId {e.NetId} without a despawn"); }
        }

        private void OnDespawned(NetworkIdentity e)
        {
            if (e == null) return;
            Despawns++;
            _live.Remove(e.NetId);
        }

        private void Update()
        {
            if (_client == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Mathf.Max(0.1f, IntervalSeconds);

            var pawn = _client.LocalPlayer;
            var origin = pawn != null ? pawn.transform.position : Vector3.zero;
            // The client's own speed, measured between probe samples rather than read off a rigidbody, so it is
            // right for a walker, a passenger and a pilot alike. A floating-origin shift moves the frame under
            // the pawn, so a jump larger than any real speed is discarded rather than read as 3 km/s.
            float speed = 0f;
            var originCell = WorldOrigin.Cell;
            if (pawn != null && _hadOrigin && originCell == _lastOriginCell)
            {
                // Only compare two samples taken in the same floating-origin frame: a shift moves the frame
                // under the pawn, so the position difference across one is not motion at all.
                float moved = Vector3.Distance(origin, _lastOrigin);
                float dt = Mathf.Max(1e-3f, Time.unscaledTime - _lastOriginAt);
                float candidate = moved / dt;
                if (candidate < MaxPlausibleSpeed) speed = candidate;
            }
            _lastOrigin = origin;
            _lastOriginCell = originCell;
            _lastOriginAt = Time.unscaledTime;
            _hadOrigin = pawn != null;
            float limit = _exitRadius + Mathf.Max(0f, SlackMeters) + speed * Mathf.Max(0f, SlackSeconds);

            int replicas = 0, beyond = 0, orphans = 0, carrierless = 0;
            float min = float.PositiveInfinity, max = 0f;
            bool chunked = NebulaChunks.IsActive;
            var c0 = CultureInfo.InvariantCulture;
            _offenders.Clear();
            _orphanNames.Clear();
            _held.Clear();
            foreach (var e in _client.Entities) if (e != null) _held.Add(e.NetId);
            foreach (var e in _client.Entities)
            {
                if (e == null || e == pawn) continue;
                replicas++;
                var container = e.Container;
                if (container != null && container.IsDynamic)
                {
                    var carrier = container.Carrier;
                    if (carrier == null || !_held.Contains(carrier.NetId)) carrierless++;
                }
                float d = Vector3.Distance(e.transform.position, origin);
                if (d < min) min = d;
                if (d > max) max = d;
                if (pawn != null && d > limit)
                {
                    beyond++;
                    // Name the first few: "one replica is stuck" is only actionable if you can see which one,
                    // and a leak is almost always one prefab or one ownership rule rather than a random entity.
                    if (_offenders.Length < 120)
                        _offenders.Append(_offenders.Length == 0 ? "" : ",").Append(e.NetId).Append(':').Append(e.name).Append('@').Append(d.ToString("0", c0));
                }
                // Content must exist before the entity standing on it arrives (design §8). A replica whose chunk
                // is not resident means ownership and spawn traffic arrived out of order, or the client's
                // container window is narrower than its interest set.
                if (chunked && NebulaChunks.At(e.transform.position) == null)
                {
                    orphans++;
                    if (_orphanNames.Length < 120)
                        _orphanNames.Append(_orphanNames.Length == 0 ? "" : ",").Append(e.NetId).Append(':').Append(e.name);
                }
            }
            if (replicas == 0) min = 0f;
            Beyond = beyond;
            Orphans = orphans;
            Carrierless = carrierless;

            var cell = chunked ? NebulaChunks.CoordOf(origin) : WorldOrigin.Cell;
            var c = CultureInfo.InvariantCulture;
            _line.Clear();
            _line.Append("[nebula-probe] t=").Append(Time.unscaledTime.ToString("0.0", c))
                .Append(" pos=").Append(origin.x.ToString("0.0", c)).Append(',').Append(origin.y.ToString("0.0", c)).Append(',').Append(origin.z.ToString("0.0", c))
                .Append(" cell=").Append(cell.x).Append(',').Append(cell.y).Append(',').Append(cell.z)
                // The floating origin this client currently renders in. A soak asserts two things on it: that
                // it moved at all (the bot really did travel far enough to shift), and that nothing else in the
                // line went wrong when it did -- a shift that lost an entity shows up as beyond or orphans.
                .Append(" origin=").Append(WorldOrigin.Cell.x).Append(',').Append(WorldOrigin.Cell.y).Append(',').Append(WorldOrigin.Cell.z)
                .Append(" replicas=").Append(replicas)
                .Append(" dmin=").Append(min.ToString("0.0", c))
                .Append(" dmax=").Append(max.ToString("0.0", c))
                .Append(" limit=").Append(limit.ToString("0.0", c))
                .Append(" speed=").Append(speed.ToString("0.0", c))
                .Append(" beyond=").Append(beyond)
                .Append(" dup=").Append(Duplicates)
                .Append(" chunks=").Append(chunked ? NebulaChunks.LoadedCount : 0)
                .Append(" orphans=").Append(orphans)
                .Append(" carrierless=").Append(carrierless)
                .Append(" spawns=").Append(Spawns)
                .Append(" despawns=").Append(Despawns)
                .Append(" pawn=").Append(pawn != null ? 1 : 0);
            if (_offenders.Length > 0) _line.Append(" beyondIds=").Append(_offenders);
            if (_orphanNames.Length > 0) _line.Append(" orphanIds=").Append(_orphanNames);
            LastLine = _line.ToString();
            if (Log) NebulaLog.Info(LastLine);
        }
    }
}
