using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>What a driver sends each tick: throttle and steering, both in -1..1.</summary>
    public struct DriveInput : INetworkInput
    {
        public float Throttle;
        public float Steer;

        public void Serialize(NetworkWriter writer)
        {
            writer.WriteFloat(Throttle);
            writer.WriteFloat(Steer);
        }

        public void Deserialize(NetworkReader reader)
        {
            Throttle = reader.ReadFloat();
            Steer = reader.ReadFloat();
        }
    }

    /// <summary>
    /// A kinematic vehicle as a game would write one for driver prediction: a deterministic function of its state
    /// (pose, velocity and speed) and the driver's input. It steers harder the faster it goes, up to a limit, and
    /// reports what it did as <see cref="NetworkMotionState.Velocity"/>.
    /// </summary>
    public sealed class TestVehicle : PredictedBehaviour<DriveInput>
    {
        public const float Acceleration = 8f;
        public const float Drag = 0.4f;
        public const float MaxYawRate = 60f;

        /// <summary>The driver's hands, read on the predicting client.</summary>
        public Func<DriveInput> Hands;
        /// <summary>What the vehicle does with nobody driving it: the worker's input.</summary>
        public DriveInput Parked = new DriveInput { Throttle = 0f, Steer = 0f };
        public float Speed;
        public int ServerInputTicks;
        public int DriverChanges;
        public ulong LastDriverSeen;

        protected override DriveInput GatherInput() => Hands != null ? Hands() : default;

        protected override DriveInput GatherServerInput(uint tick)
        {
            ServerInputTicks++;
            return Parked;
        }

        protected override void Simulate(uint tick, in DriveInput input, float deltaTime)
        {
            Speed += (Mathf.Clamp(input.Throttle, -1f, 1f) * Acceleration - Drag * Speed) * deltaTime;
            float grip = Mathf.Clamp01(Mathf.Abs(Speed) / 4f);
            float yaw = Mathf.Clamp(input.Steer, -1f, 1f) * MaxYawRate * grip * deltaTime;
            var rotation = Identity.LocalRotation * Quaternion.Euler(0f, yaw, 0f);
            var velocity = rotation * Vector3.forward * Speed;
            Identity.SetLocalPose(Identity.Container, Identity.LocalPosition + velocity * deltaTime, rotation);
            // Velocity in the space the pose is in; Nebula converts it when the vehicle changes space.
            Identity.Motion.Velocity = Identity.Container != null ? Identity.Container.Rotation * velocity : velocity;
        }

        protected override void WriteState(NetworkWriter writer)
        {
            base.WriteState(writer);
            writer.WriteFloat(Speed);
        }

        protected override void ReadState(NetworkReader reader)
        {
            base.ReadState(reader);
            Speed = reader.ReadFloat();
        }

        protected override void OnDriverChanged(ulong previous, ulong current)
        {
            DriverChanges++;
            LastDriverSeen = current;
        }
    }

    /// <summary>
    /// The measurement behind <c>docs/driven-vehicles.md</c>: how a vehicle feels to the player driving it, at a given
    /// latency, when the worker simulates it and the driver sees the interpolated stream (today's path for anything the
    /// player does not own), and when the driver's client predicts it (driver prediction).
    /// <para>
    /// One process, virtual time. The worker's copy and the client's copy are two real <see cref="TestVehicle"/>s, and
    /// every step between them is Nebula's own code: the worker consumes the input with
    /// <see cref="PredictedBehaviourBase.ServerReceiveInput"/> and <see cref="PredictedBehaviour{TInput}.NetworkTick"/>,
    /// the interpolated view is a real <see cref="RemoteInterpolator"/>, the predicted one is
    /// <see cref="PredictedBehaviourBase.ClientPredictTick"/> reconciled with
    /// <see cref="PredictedBehaviourBase.ClientReconcile"/>. The link is a one-way delay of half the round trip plus a
    /// uniform jitter, sequenced (a message older than the newest delivered is dropped). The client's clocks follow
    /// <see cref="NebulaClient"/>: the server tick estimate anchored on the newest snapshot plus half the RTT, the render
    /// tick <see cref="NebulaConfig.InterpolationDelayTicks"/> behind the newest snapshot with its offset smoothed at
    /// 3/s, and inputs led by half the RTT plus <see cref="NebulaConfig.InputLeadMarginTicks"/> plus the adaptive
    /// adjustment towards <see cref="NebulaConfig.InputLeadTargetTicks"/>. Frames are 60 per second, each a
    /// FixedUpdate then an Update, at a phase that is not the worker's.
    /// </para>
    /// </summary>
    public sealed class DrivenVehicleHarness : IDisposable
    {
        public enum Mode { ServerSimulated, DriverPredicted }

        public struct Settings
        {
            public Mode Mode;
            public int RttMs;
            /// <summary>Plus or minus this much on each one-way trip, uniformly.</summary>
            public float JitterMs;
            public int Seed;
            /// <summary>The driver: time in seconds and what they see (the camera's yaw) in, input out.</summary>
            public Func<double, float, DriveInput> Driver;
            public double Seconds;
            /// <summary>Server-side only: at this time the worker's vehicle is shoved sideways (a collision the client's model does not know about). Negative: never.</summary>
            public double BumpAt;
            public float BumpMeters;
        }

        /// <summary>One rendered frame, as the driver saw it, and where the vehicle really was on the worker at that moment.</summary>
        public struct Frame
        {
            public double Time;
            public Vector3 Shown;
            public float ShownYaw;
            public Vector3 Camera;
            public float CameraYaw;
            public Vector3 Truth;
            public float TruthYaw;
            public DriveInput Input;
        }

        public readonly List<Frame> Frames = new List<Frame>();
        public int Corrections => _client.Corrections;
        public float MaxCorrection => _client.MaxCorrectionMagnitude;
        /// <summary>Inputs the worker had to repeat because they arrived too late, after the first second.</summary>
        public int InputsMissed => _server.InputsMissed - _missedAtSettle;
        private int _missedAtSettle;
        /// <summary>Corrections and missed inputs are counted from here: before it the input lead is still finding its level.</summary>
        public const double SettleSeconds = 1.0;

        private const double Tick = NetworkTime.TickInterval;
        private const int InterpolationDelayTicks = 3;
        private const int InputLeadMarginTicks = 2;
        private const int InputLeadTargetTicks = 3;
        private const double RenderOffsetRate = 3.0;
        /// <summary>Chase camera: follows the shown pose with this time constant, as a game's camera smoothing does.</summary>
        public const float CameraSmoothing = 0.08f;

        private readonly Settings _s;
        private readonly System.Random _random;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly TestVehicle _server, _client;
        private readonly RemoteInterpolator _view;

        private struct Packet { public double At; public long Seq; public int Kind; public uint Tick; public byte[] Body; public int Lead; }
        private readonly List<Packet> _toServer = new List<Packet>(), _toClient = new List<Packet>();
        private long _seqUp, _seqDown, _newestUpDelivered = -1, _newestDownDelivered = -1;

        // client clocks (NebulaClient)
        private uint _latestServerTick;
        private double _anchor, _anchorTime = -1, _renderOffset;
        private bool _hasRenderOffset;
        private uint _predictTick;
        private int _leadAdjust;
        private double _leadHoldUntil;
        private readonly List<(uint tick, byte[] payload)> _recentInputs = new List<(uint, byte[])>();

        // server
        private uint _serverTick;
        private int _leadMin = int.MaxValue;

        // camera
        private Vector3 _camera;
        private float _cameraYaw;
        private bool _hasCamera;
        private DriveInput _hands;

        public DrivenVehicleHarness(Settings settings)
        {
            _s = settings;
            _random = new System.Random(settings.Seed);
            _server = Make("vehicle-on-the-worker");
            _server.Identity.OwnerClientId = 1; // the input stream a player's worker consumes: arrival and repeat rules
            _server.Identity.HasAuthority = true;
            _client = Make("vehicle-on-the-client");
            _client.Hands = () => _hands;
            var viewGo = new GameObject("interpolated-view");
            _objects.Add(viewGo);
            _view = viewGo.AddComponent<RemoteInterpolator>();
        }

        private TestVehicle Make(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            var v = go.AddComponent<TestVehicle>();
            go.GetComponent<NetworkIdentity>().Initialize();
            v.Speed = 0f;
            return v;
        }

        public void Dispose()
        {
            foreach (var go in _objects) if (go != null) Object.DestroyImmediate(go);
            _objects.Clear();
        }

        private double OneWay() => _s.RttMs * 0.5 / 1000.0 + (_random.NextDouble() * 2 - 1) * _s.JitterMs / 1000.0;
        private double HalfRttTicks => _s.RttMs > 0 ? _s.RttMs * 0.5 / 1000.0 * NetworkTime.TickRate : 0.5;

        public void Run()
        {
            // The worker ticks at k/60; the client's frames sit 0.37 of a tick later, so nothing lines up by accident.
            const double ClientPhase = 0.37 * Tick;
            double end = _s.Seconds;
            _serverTick = 1000;
            double serverEpoch = -_serverTick * Tick; // server tick k happens at time serverEpoch + k * Tick
            double nextServer = serverEpoch + (_serverTick + 1) * Tick;
            double nextFrame = ClientPhase;
            bool bumped = false;
            while (nextFrame < end)
            {
                if (nextServer <= nextFrame)
                {
                    double now = nextServer;
                    DeliverToServer(now);
                    _serverTick++;
                    if (!bumped && _s.BumpAt >= 0 && now >= _s.BumpAt)
                    {
                        bumped = true;
                        _server.transform.position += _server.transform.right * _s.BumpMeters;
                    }
                    NetworkTime.Tick = _serverTick;
                    _server.NetworkTick(_serverTick, NetworkTime.TickInterval);
                    Publish(now);
                    nextServer += Tick;
                    continue;
                }
                ClientFrame(nextFrame, serverEpoch);
                // The first second is the connection settling (the input lead finding its level): not driving.
                if (nextFrame < SettleSeconds && nextFrame + Tick >= SettleSeconds) { _client.ResetCorrectionStats(); _missedAtSettle = _server.InputsMissed; }
                nextFrame += Tick;
            }
        }

        private void DeliverToServer(double now)
        {
            _toServer.Sort((a, b) => a.At.CompareTo(b.At));
            int i = 0;
            for (; i < _toServer.Count && _toServer[i].At <= now; i++)
            {
                var p = _toServer[i];
                if (p.Seq < _newestUpDelivered) continue; // sequenced: an older packet behind a newer one is dropped
                _newestUpDelivered = p.Seq;
                var r = new NetworkReader(p.Body);
                int n = r.ReadByte();
                uint newest = 0;
                for (int f = 0; f < n; f++)
                {
                    uint tick = r.ReadUInt();
                    var payload = r.ReadBytes();
                    _server.ServerReceiveInput(tick, new NetworkReader(payload));
                    if (tick > newest) newest = tick;
                }
                // Received before the tick is simulated: a lead of 1 is just in time.
                int lead = (int)((long)newest - _serverTick);
                if (lead < _leadMin) _leadMin = lead;
            }
            _toServer.RemoveRange(0, i);
        }

        private void Publish(double now)
        {
            var w = new NetworkWriter(128);
            if (_s.Mode == Mode.DriverPredicted)
            {
                _server.WriteOwnerState(w);
                int lead = _leadMin == int.MaxValue ? int.MaxValue : _leadMin;
                _leadMin = int.MaxValue;
                _toClient.Add(new Packet { At = now + OneWay(), Seq = _seqDown++, Kind = 1, Tick = _serverTick, Body = w.ToArray(), Lead = lead });
            }
            else
            {
                w.WriteVector3(_server.transform.position);
                w.WriteQuaternion(_server.transform.rotation);
                w.WriteVector3(_server.Identity.Motion.Velocity);
                int lead = _leadMin;
                _leadMin = int.MaxValue;
                _toClient.Add(new Packet { At = now + OneWay(), Seq = _seqDown++, Kind = 0, Tick = _serverTick, Body = w.ToArray(), Lead = lead });
            }
        }

        private void NoteServerTick(uint tick, double now)
        {
            if (tick <= _latestServerTick) return;
            _latestServerTick = tick;
            double fresh = tick + HalfRttTicks;
            double current = _anchor + (now - _anchorTime) * NetworkTime.TickRate;
            if (_anchorTime < 0 || Math.Abs(fresh - current) > 3) _anchor = fresh;
            else _anchor = current + (fresh - current) * 0.1;
            _anchorTime = now;
        }

        private void NoteLead(int lead, double now)
        {
            if (lead == int.MaxValue || now < _leadHoldUntil) return;
            if (lead < InputLeadTargetTicks)
            {
                _leadAdjust += Math.Min(InputLeadTargetTicks - lead, 4);
                _leadHoldUntil = now + Math.Max(0.1, _s.RttMs / 1000.0) + 0.1;
            }
        }

        private void ClientFrame(double now, double serverEpoch)
        {
            // Update's receive half first: everything that has arrived by now.
            _toClient.Sort((a, b) => a.At.CompareTo(b.At));
            int i = 0;
            for (; i < _toClient.Count && _toClient[i].At <= now; i++)
            {
                var p = _toClient[i];
                if (p.Seq < _newestDownDelivered) continue;
                _newestDownDelivered = p.Seq;
                NoteServerTick(p.Tick, now);
                NoteLead(p.Lead, now);
                var r = new NetworkReader(p.Body);
                if (p.Kind == 1) _client.ClientReconcile(p.Tick, r);
                else _view.Push(p.Tick, null, r.ReadVector3(), r.ReadQuaternion(), r.ReadVector3());
            }
            _toClient.RemoveRange(0, i);
            if (_anchorTime < 0) return; // nothing heard yet

            double estimate = _anchor + (now - _anchorTime) * NetworkTime.TickRate;

            // The driver looks at the camera, then acts: this frame's input.
            _hands = _s.Driver(now, _hasCamera ? _cameraYaw : 0f);

            // FixedUpdate: gather and send the input for the tick it should reach the worker in time for.
            int leadTicks = (int)Math.Ceiling(_s.RttMs * 0.5 / 1000.0 * NetworkTime.TickRate) + InputLeadMarginTicks + _leadAdjust;
            uint target = (uint)Math.Round(estimate) + (uint)leadTicks;
            int steps = 1;
            uint first;
            if (_predictTick == 0 || target > _predictTick + 8) first = target;
            else if (target + 4 < _predictTick) first = 0;
            else { first = _predictTick + 1; if (target > _predictTick + 1) steps = 2; }
            if (first != 0)
            {
                for (int s = 0; s < steps; s++)
                {
                    _predictTick = first + (uint)s;
                    var w = new NetworkWriter(32);
                    if (_s.Mode == Mode.DriverPredicted) _client.ClientPredictTick(_predictTick, w);
                    else _hands.Serialize(w); // today: the input rides to the worker, nothing is simulated here
                    _recentInputs.Add((_predictTick, w.ToArray()));
                    while (_recentInputs.Count > 3) _recentInputs.RemoveAt(0);
                }
                var msg = new NetworkWriter(64);
                msg.WriteByte((byte)_recentInputs.Count);
                foreach (var (tick, payload) in _recentInputs) { msg.WriteUInt(tick); msg.WriteBytes(payload); }
                _toServer.Add(new Packet { At = now + OneWay(), Seq = _seqUp++, Body = msg.ToArray() });
            }

            // Update's render half.
            double targetRender = estimate - HalfRttTicks - InterpolationDelayTicks;
            double nowTicks = now * NetworkTime.TickRate;
            double desired = targetRender - nowTicks;
            if (!_hasRenderOffset || Math.Abs(desired - _renderOffset) > 10) { _renderOffset = desired; _hasRenderOffset = true; }
            else _renderOffset += (desired - _renderOffset) * Math.Min(1.0, RenderOffsetRate * Tick);
            double renderTick = nowTicks + _renderOffset;

            Vector3 shown;
            Quaternion shownRotation;
            if (_s.Mode == Mode.DriverPredicted)
            {
                shown = _client.transform.position;
                shownRotation = _client.transform.rotation;
            }
            else if (!_view.Sample(renderTick, out shown, out shownRotation)) return;

            float shownYaw = Yaw(shownRotation);
            float k = 1f - Mathf.Exp(-(float)Tick / CameraSmoothing);
            if (!_hasCamera) { _camera = shown; _cameraYaw = shownYaw; _hasCamera = true; }
            else
            {
                _camera = Vector3.Lerp(_camera, shown, k);
                _cameraYaw += Mathf.DeltaAngle(_cameraYaw, shownYaw) * k;
            }

            // Where the vehicle really is at this moment: the worker's pose, advanced by the part of a tick since.
            double serverNow = (now - serverEpoch) / Tick;
            double since = (serverNow - _serverTick) * Tick;
            var truth = _server.transform.position + _server.Identity.Motion.Velocity * (float)since;
            Frames.Add(new Frame
            {
                Time = now, Shown = shown, ShownYaw = shownYaw, Camera = _camera, CameraYaw = _cameraYaw,
                Truth = truth, TruthYaw = Yaw(_server.transform.rotation), Input = _hands,
            });
        }

        public static float Yaw(Quaternion q) => q.eulerAngles.y;

        // ------------------------------------------------------------------------------------ the measurements

        public struct Measurement
        {
            public double ResponseMs;
            public double CameraResponseMs;
            public float OvershootDegrees;
            public double SettleMs;
            public float JitterMetersPerSecond;
            public float WorstStepCm;
            public int Corrections;
            public float MaxCorrectionM;
        }

        /// <summary>The first frame at or after <paramref name="at"/> whose shown yaw moved more than half a degree from the yaw shown then; the time from the input to it.</summary>
        public double ResponseAfter(double at, bool camera)
        {
            float? before = null;
            foreach (var f in Frames)
            {
                if (f.Time < at) continue;
                float yaw = camera ? f.CameraYaw : f.ShownYaw;
                if (before == null) { before = yaw; continue; }
                if (Mathf.Abs(Mathf.DeltaAngle(before.Value, yaw)) > 0.5f) return (f.Time - at) * 1000.0;
            }
            return double.NaN;
        }

        /// <summary>
        /// Frame-to-frame roughness of what the driver sees, between two times: the RMS, in cm, of the shown pose's
        /// second difference minus the true pose's (the change of the step from one frame to the next that the
        /// vehicle's own motion does not explain). A smooth view scores 0 whatever its delay.
        /// </summary>
        public float Jitter(double from, double to)
        {
            double sum = 0;
            int n = 0;
            for (int i = 2; i < Frames.Count; i++)
            {
                if (Frames[i].Time < from || Frames[i].Time > to) continue;
                var excess = SecondDifference(i, shown: true) - SecondDifference(i, shown: false);
                sum += excess.sqrMagnitude;
                n++;
            }
            return n > 0 ? (float)Math.Sqrt(sum / n) * 100f : 0f;
        }

        /// <summary>The largest single-frame hitch between two times, in cm: the worst second difference of the shown pose beyond the true one's (a correction snapping the view, or a stall).</summary>
        public float WorstStep(double from, double to)
        {
            float worst = 0f;
            for (int i = 2; i < Frames.Count; i++)
            {
                if (Frames[i].Time < from || Frames[i].Time > to) continue;
                float excess = (SecondDifference(i, shown: true) - SecondDifference(i, shown: false)).magnitude * 100f;
                if (excess > worst) worst = excess;
            }
            return worst;
        }

        private Vector3 SecondDifference(int i, bool shown)
        {
            Vector3 P(int j) => shown ? Frames[j].Shown : Frames[j].Truth;
            return P(i) - 2f * P(i - 1) + P(i - 2);
        }
    }
}
