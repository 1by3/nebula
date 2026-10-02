using System.Globalization;
using System.Numerics;

namespace Nebula.LoadGen;

/// <summary>The behaviours that ship with nebula-loadgen. None knows a game type: they move, press action bits and send RPCs the options name.</summary>
public static class BuiltinBehaviours
{
    public static void Register(IBehaviourRegistry r)
    {
        r.Add("idle", _ => new Idle());
        r.Add("wander", o => new Wander(o));
        r.Add("path", o => new Path(o));
        r.Add("burst", o => new Burst(o));
        r.Add("travel", o => new Travel(o));
        r.Add("rpc", o => new RpcAtRate(o));
        r.Add("churn", o => new Churn(o));
    }

    /// <summary>Stands still and sends empty inputs.</summary>
    public sealed class Idle : ILoadGenBehaviour
    {
        public void Tick(IClientContext client, double dt) { }
    }

    /// <summary>
    /// Random walk inside a radius of where the pawn spawned, with stops and sprints. Options: <c>radius</c> (30 m),
    /// <c>stop-chance</c> (0.25, the chance a leg ends in a stop), <c>stop-min</c>/<c>stop-max</c> (1 and 4 s),
    /// <c>sprint-chance</c> (0.2), <c>leg-max</c> (8 s, a leg that has not arrived by then ends).
    /// </summary>
    public sealed class Wander : ILoadGenBehaviour
    {
        private readonly double _radius, _stopChance, _stopMin, _stopMax, _sprintChance, _legMax;
        private Vector3 _home, _target;
        private bool _hasHome, _walking, _sprint;
        private double _until;

        public Wander(BehaviourOptions o)
        {
            _radius = Math.Max(1, o.GetDouble("radius", 30));
            _stopChance = o.GetDouble("stop-chance", 0.25);
            _stopMin = o.GetDouble("stop-min", 1);
            _stopMax = Math.Max(_stopMin, o.GetDouble("stop-max", 4));
            _sprintChance = o.GetDouble("sprint-chance", 0.2);
            _legMax = Math.Max(1, o.GetDouble("leg-max", 8));
        }

        public void OnJoined(IClientContext c) { _hasHome = false; _walking = false; _until = 0; }

        public void Tick(IClientContext c, double dt)
        {
            if (!c.Joined) return;
            if (!_hasHome) { _home = c.EstimatedPosition; _hasHome = true; }
            double now = c.Time;
            var pos = c.EstimatedPosition;
            if (_walking && (Dist(pos, _target) < 0.75 || now >= _until)) { _walking = false; c.SetMove(Vector2.Zero); StopOrChoose(c, now); return; }
            if (!_walking && now >= _until) StopOrChoose(c, now);
            if (_walking) c.SetMove(Toward(pos, _target), _sprint);
        }

        private void StopOrChoose(IClientContext c, double now)
        {
            var rng = c.Rng;
            if (rng.NextDouble() < _stopChance)
            {
                _until = now + _stopMin + rng.NextDouble() * (_stopMax - _stopMin);
                c.SetMove(Vector2.Zero);
                return;
            }
            double angle = rng.NextDouble() * Math.PI * 2, d = Math.Sqrt(rng.NextDouble()) * _radius;
            _target = new Vector3(_home.X + (float)(Math.Cos(angle) * d), _home.Y, _home.Z + (float)(Math.Sin(angle) * d));
            _sprint = rng.NextDouble() < _sprintChance;
            _walking = true;
            _until = now + _legMax;
        }
    }

    /// <summary>
    /// Follows waypoints. Options: <c>points</c> (<c>x,z|x,z|...</c>, required), <c>relative</c> (true: offsets from the
    /// spawn point), <c>mode</c> (<c>loop</c>, <c>pingpong</c> or <c>once</c>; default loop), <c>arrive</c> (1 m),
    /// <c>pause</c> (seconds to stand at each point, 0), <c>sprint</c> (false).
    /// </summary>
    public sealed class Path : ILoadGenBehaviour
    {
        private readonly List<Vector3> _points = new();
        private readonly bool _relative, _sprint;
        private readonly string _mode;
        private readonly double _arrive, _pause;
        private Vector3 _home;
        private int _next, _step = 1;
        private bool _hasHome, _done;
        private double _pausedUntil;
        private string _error = "";

        public Path(BehaviourOptions o)
        {
            _relative = o.GetBool("relative", true);
            _sprint = o.GetBool("sprint", false);
            _mode = o.GetString("mode", "loop").ToLowerInvariant();
            _arrive = Math.Max(0.1, o.GetDouble("arrive", 1));
            _pause = o.GetDouble("pause", 0);
            foreach (string p in o.GetList("points"))
            {
                var xy = p.Split(',', StringSplitOptions.TrimEntries);
                if (xy.Length == 2 && float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    _points.Add(new Vector3(x, 0, z));
                else _error = "path: bad waypoint '" + p + "', want x,z";
            }
            if (_points.Count == 0 && _error.Length == 0) _error = "path: the points option is required";
        }

        public void OnJoined(IClientContext c) { _hasHome = false; _next = 0; _step = 1; _done = false; _pausedUntil = 0; }

        public void Tick(IClientContext c, double dt)
        {
            if (_error.Length > 0) { c.ReportError(_error); _error = ""; return; }
            if (!c.Joined || _points.Count == 0 || _done) return;
            if (!_hasHome) { _home = c.EstimatedPosition; _hasHome = true; }
            if (c.Time < _pausedUntil) { c.SetMove(Vector2.Zero); return; }
            var pos = c.EstimatedPosition;
            var target = _relative ? new Vector3(_home.X + _points[_next].X, _home.Y, _home.Z + _points[_next].Z) : new Vector3(_points[_next].X, pos.Y, _points[_next].Z);
            if (Dist(pos, target) <= _arrive)
            {
                c.SetMove(Vector2.Zero);
                _pausedUntil = c.Time + _pause;
                Advance();
                return;
            }
            c.SetMove(Toward(pos, target), _sprint);
        }

        private void Advance()
        {
            if (_points.Count == 1) { if (_mode == "once") _done = true; return; }
            int n = _next + _step;
            if (n >= 0 && n < _points.Count) { _next = n; return; }
            switch (_mode)
            {
                case "once": _done = true; break;
                case "pingpong": _step = -_step; _next += _step; break;
                default: _next = 0; break;
            }
        }
    }

    /// <summary>
    /// Presses an action bit on a rhythm, such as a trigger held for a moment every second. Options: <c>bit</c> (0),
    /// <c>every</c> (1 s between presses), <c>hold</c> (0.1 s), <c>jitter</c> (0.2: each gap varies by that fraction).
    /// Each press counts as one action.
    /// </summary>
    public sealed class Burst : ILoadGenBehaviour
    {
        private readonly uint _mask;
        private readonly double _every, _hold, _jitter;
        private double _nextPress = -1, _releaseAt;
        private bool _down;

        public Burst(BehaviourOptions o)
        {
            _mask = 1u << Math.Clamp(o.GetInt("bit", 0), 0, 31);
            _every = Math.Max(0.01, o.GetDouble("every", 1));
            _hold = Math.Max(0, o.GetDouble("hold", 0.1));
            _jitter = Math.Clamp(o.GetDouble("jitter", 0.2), 0, 1);
        }

        public void OnLeft(IClientContext c) { _down = false; _nextPress = -1; }

        public void Tick(IClientContext c, double dt)
        {
            if (!c.Joined) return;
            double now = c.Time;
            if (_nextPress < 0) _nextPress = now + c.Rng.NextDouble() * _every;
            if (_down && now >= _releaseAt) { _down = false; c.SetActions(0); }
            if (!_down && now >= _nextPress)
            {
                _down = true;
                _releaseAt = now + _hold;
                c.SetActions(_mask);
                c.CountAction();
                _nextPress = now + _every * (1 + (c.Rng.NextDouble() * 2 - 1) * _jitter);
            }
        }
    }

    /// <summary>
    /// Asks the game for a scope change every so often through the plugin's <see cref="ScopeTravelHandler"/>. Options:
    /// <c>every-minutes</c> (5; the first request lands at a random point inside the first interval, so clients do not
    /// all travel at once), <c>targets</c> (<c>a|b|c</c>, passed to the handler), <c>order</c> (<c>random</c> or <c>cycle</c>).
    /// </summary>
    public sealed class Travel : ILoadGenBehaviour
    {
        private readonly double _every;
        private readonly string[] _targets;
        private readonly bool _cycle;
        private double _next = -1;
        private int _i;

        public Travel(BehaviourOptions o)
        {
            _every = Math.Max(0.05, o.GetDouble("every-minutes", 5) * 60);
            _targets = o.GetList("targets");
            _cycle = o.GetString("order", "random").Equals("cycle", StringComparison.OrdinalIgnoreCase);
        }

        public void OnLeft(IClientContext c) => _next = -1;

        public void Tick(IClientContext c, double dt)
        {
            if (!c.Joined) return;
            if (_next < 0) _next = c.Time + c.Rng.NextDouble() * _every;
            if (c.Time < _next) return;
            _next = c.Time + _every;
            if (_targets.Length == 0) { c.RequestScopeTravel(""); return; }
            string target = _cycle ? _targets[_i++ % _targets.Length] : _targets[c.Rng.Next(_targets.Length)];
            c.RequestScopeTravel(target);
        }
    }

    /// <summary>
    /// Sends a server RPC at a rate. Options: <c>entity</c> (<c>pawn</c> or a net id; default pawn), <c>behaviour</c>
    /// (index of the behaviour on the entity, 0), <c>method</c> (a name, or <c>0x</c> plus the hash; required),
    /// <c>args</c> (hex bytes, empty), <c>rate</c> (per second, 1). Gaps are exponential, so the stream is Poisson.
    /// </summary>
    public sealed class RpcAtRate : ILoadGenBehaviour
    {
        private readonly string _entity, _method;
        private readonly byte _behaviour;
        private readonly byte[] _args;
        private readonly double _rate;
        private double _next = -1;
        private string _error = "";

        public RpcAtRate(BehaviourOptions o)
        {
            _entity = o.GetString("entity", "pawn");
            _behaviour = (byte)Math.Clamp(o.GetInt("behaviour", 0), 0, 255);
            _method = o.GetString("method", "");
            _rate = Math.Max(0.001, o.GetDouble("rate", 1));
            try { _args = Convert.FromHexString(o.GetString("args", "")); }
            catch (FormatException) { _args = Array.Empty<byte>(); _error = "rpc: args must be hex"; }
            if (_method.Length == 0 && _error.Length == 0) _error = "rpc: the method option is required";
        }

        public void OnLeft(IClientContext c) => _next = -1;

        public void Tick(IClientContext c, double dt)
        {
            if (_error.Length > 0) { c.ReportError(_error); _error = ""; return; }
            if (_method.Length == 0 || !c.Joined) return;
            double now = c.Time;
            if (_next < 0) _next = now + c.Rng.NextDouble() / _rate;
            // A stall must not become a burst: send at most one call per tick, then move on from now.
            if (now < _next) return;
            _next = now + -Math.Log(1 - c.Rng.NextDouble()) / _rate;
            ulong id = _entity.Equals("pawn", StringComparison.OrdinalIgnoreCase) ? 0 : ulong.Parse(_entity, CultureInfo.InvariantCulture);
            if (_method.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(_method.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                c.SendServerRpc(id, _behaviour, hash, _args);
            else c.SendServerRpc(id, _behaviour, _method, _args);
        }
    }

    /// <summary>
    /// Joins and leaves in waves, like a lobby filling and emptying. After <c>on</c> seconds connected (60, each client's
    /// first stay shortened by a random phase) it says goodbye and rejoins after <c>off</c> seconds (10). <c>jitter</c>
    /// (0.5) varies both. Each leave counts as one action.
    /// </summary>
    public sealed class Churn : ILoadGenBehaviour
    {
        private readonly double _on, _off, _jitter;
        private double _leaveAt = -1;
        private bool _first = true;

        public Churn(BehaviourOptions o)
        {
            _on = Math.Max(1, o.GetDouble("on", 60));
            _off = Math.Max(0, o.GetDouble("off", 10));
            _jitter = Math.Clamp(o.GetDouble("jitter", 0.5), 0, 1);
        }

        public void OnJoined(IClientContext c)
        {
            double stay = _on * (1 + (c.Rng.NextDouble() * 2 - 1) * _jitter);
            if (_first) stay *= c.Rng.NextDouble();
            _first = false;
            _leaveAt = c.Time + Math.Max(0.5, stay);
        }

        public void Tick(IClientContext c, double dt)
        {
            if (_leaveAt < 0 || !c.Joined || c.Time < _leaveAt) return;
            _leaveAt = -1;
            c.CountAction();
            c.Leave(_off * (1 + (c.Rng.NextDouble() * 2 - 1) * _jitter));
        }
    }

    internal static float Dist(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    internal static Vector2 Toward(Vector3 from, Vector3 to)
    {
        var d = new Vector2(to.X - from.X, to.Z - from.Z);
        float len = d.Length();
        return len < 1e-4f ? Vector2.Zero : d / len;
    }
}
