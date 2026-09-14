using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// The control plane as one JSON document, and one control-plane write as one JSON object. This is the wire
    /// format between <see cref="RemoteControlPlane"/> (workers, gateways) and <see cref="ControlPlaneHost"/> (the
    /// orchestrator), and the format the host stores through <see cref="IControlPlaneStorage"/>.
    /// <para>
    /// Snapshot: <c>{"version":N,"now":unixMs,"workers":[...],"leases":[...],"gateways":[...],"settings":{...}}</c>.
    /// Write: <c>{"op":"RegisterWorker","workerId":"w1",...}</c>; a request carries a list of them under
    /// <c>"ops"</c>, applied in order. Times are Unix milliseconds; vectors are <c>[x, y, z]</c>.
    /// </para>
    /// </summary>
    public static class ControlPlaneJson
    {
        // ---------------------------------------------------------------------------------------- snapshot

        /// <summary>Everything a subscriber needs, as plain lists (see <see cref="Parse"/>).</summary>
        public sealed class Snapshot
        {
            public long Version;
            public DateTime Now;
            public List<WorkerInfo> Workers = new List<WorkerInfo>();
            public List<LeaseInfo> Leases = new List<LeaseInfo>();
            public List<GatewayInfo> Gateways = new List<GatewayInfo>();
            public Dictionary<string, string> Settings = new Dictionary<string, string>();
        }

        public static string Write(long version, DateTime now, IReadOnlyList<WorkerInfo> workers, IReadOnlyList<LeaseInfo> leases, IReadOnlyList<GatewayInfo> gateways, IReadOnlyDictionary<string, string> settings)
        {
            var sb = new StringBuilder(4096);
            var w = new JsonWriter(sb);
            w.BeginObject();
            w.Prop("version", version);
            w.Prop("now", ToUnixMs(now));
            w.Key("workers");
            w.BeginArray();
            for (int i = 0; i < workers.Count; i++)
            {
                var x = workers[i];
                w.BeginObject();
                w.Prop("workerId", x.WorkerId ?? "");
                w.Prop("workerIndex", x.WorkerIndex);
                w.Prop("address", x.Address ?? "");
                w.Prop("port", (int)x.Port);
                w.Prop("status", x.Status ?? "");
                w.Prop("lastHeartbeat", ToUnixMs(x.LastHeartbeat));
                w.Prop("tickCount", x.TickCount);
                w.Key("tickMs"); Num(w, x.TickMs);
                w.Prop("entityCount", x.EntityCount);
                w.Prop("authoritativeCount", x.AuthoritativeCount);
                w.Prop("ghostCount", x.GhostCount);
                w.Prop("playerCount", x.PlayerCount);
                w.Prop("botCount", x.BotCount);
                w.Prop("serverDrivenCount", x.ServerDrivenCount);
                w.EndObject();
            }
            w.EndArray();
            w.Key("leases");
            w.BeginArray();
            for (int i = 0; i < leases.Count; i++)
            {
                var l = leases[i];
                w.BeginObject();
                w.Prop("containerId", l.ContainerId ?? "");
                w.Prop("workerId", l.WorkerId ?? "");
                w.Prop("epoch", l.Epoch);
                w.Prop("state", l.State ?? "");
                w.Prop("updatedAt", ToUnixMs(l.UpdatedAt));
                w.Prop("hasBounds", l.HasBounds);
                if (l.HasBounds)
                {
                    w.Key("center"); Vec(w, l.BoundsCenter);
                    w.Key("size"); Vec(w, l.BoundsSize);
                }
                w.EndObject();
            }
            w.EndArray();
            w.Key("gateways");
            w.BeginArray();
            for (int i = 0; i < gateways.Count; i++)
            {
                var g = gateways[i];
                w.BeginObject();
                w.Prop("gatewayId", g.GatewayId ?? "");
                w.Prop("address", g.Address ?? "");
                w.Prop("port", (int)g.Port);
                w.Prop("lastHeartbeat", ToUnixMs(g.LastHeartbeat));
                w.EndObject();
            }
            w.EndArray();
            w.Key("settings");
            w.BeginObject();
            if (settings != null)
            {
                foreach (var kv in settings) w.Prop(kv.Key, kv.Value ?? "");
            }
            w.EndObject();
            w.EndObject();
            return sb.ToString();
        }

        /// <summary>Read a snapshot written by <see cref="Write"/>. Throws <see cref="FormatException"/> on malformed input.</summary>
        public static Snapshot Parse(string json)
        {
            if (!PersistenceJson.TryParseObject(json, out var root, out string error)) throw new FormatException(error);
            var s = new Snapshot
            {
                Version = (long)Num(root, "version"),
                Now = FromUnixMs(Num(root, "now")),
            };
            if (root.TryGetValue("workers", out var workers) && workers is List<object> wl)
            {
                foreach (var item in wl)
                {
                    if (!(item is Dictionary<string, object> o)) continue;
                    s.Workers.Add(new WorkerInfo
                    {
                        WorkerId = Str(o, "workerId"),
                        WorkerIndex = (uint)Num(o, "workerIndex"),
                        Address = Str(o, "address"),
                        Port = (ushort)Num(o, "port"),
                        Status = Str(o, "status"),
                        LastHeartbeat = FromUnixMs(Num(o, "lastHeartbeat")),
                        TickCount = (ulong)Num(o, "tickCount"),
                        TickMs = (float)Num(o, "tickMs"),
                        EntityCount = (uint)Num(o, "entityCount"),
                        AuthoritativeCount = (uint)Num(o, "authoritativeCount"),
                        GhostCount = (uint)Num(o, "ghostCount"),
                        PlayerCount = (uint)Num(o, "playerCount"),
                        BotCount = (uint)Num(o, "botCount"),
                        ServerDrivenCount = (uint)Num(o, "serverDrivenCount"),
                    });
                }
            }
            if (root.TryGetValue("leases", out var leases) && leases is List<object> ll)
            {
                foreach (var item in ll)
                {
                    if (!(item is Dictionary<string, object> o)) continue;
                    var l = new LeaseInfo
                    {
                        ContainerId = Str(o, "containerId"),
                        WorkerId = Str(o, "workerId"),
                        Epoch = (ulong)Num(o, "epoch"),
                        State = Str(o, "state"),
                        UpdatedAt = FromUnixMs(Num(o, "updatedAt")),
                        HasBounds = Bool(o, "hasBounds"),
                    };
                    if (l.HasBounds)
                    {
                        l.BoundsCenter = Vec(o, "center");
                        l.BoundsSize = Vec(o, "size");
                    }
                    s.Leases.Add(l);
                }
            }
            if (root.TryGetValue("gateways", out var gateways) && gateways is List<object> gl)
            {
                foreach (var item in gl)
                {
                    if (!(item is Dictionary<string, object> o)) continue;
                    s.Gateways.Add(new GatewayInfo
                    {
                        GatewayId = Str(o, "gatewayId"),
                        Address = Str(o, "address"),
                        Port = (ushort)Num(o, "port"),
                        LastHeartbeat = FromUnixMs(Num(o, "lastHeartbeat")),
                    });
                }
            }
            if (root.TryGetValue("settings", out var settings) && settings is Dictionary<string, object> sd)
            {
                foreach (var kv in sd) s.Settings[kv.Key] = PersistenceJson.AsString(kv.Value) ?? "";
            }
            return s;
        }

        // ---------------------------------------------------------------------------------------- writes

        /// <summary>Names of the writes, as they travel on the wire.</summary>
        public const string RegisterWorker = "RegisterWorker", HeartbeatWorker = "HeartbeatWorker", UnregisterWorker = "UnregisterWorker",
            RegisterGateway = "RegisterGateway", HeartbeatGateway = "HeartbeatGateway", UnregisterGateway = "UnregisterGateway",
            HeartbeatOrchestrator = "HeartbeatOrchestrator", SetSetting = "SetSetting",
            EnsureContainer = "EnsureContainer", EnsureRuntimeContainer = "EnsureRuntimeContainer", TouchContainer = "TouchContainer",
            AssignContainer = "AssignContainer", PinContainer = "PinContainer", SetLeaseState = "SetLeaseState",
            ReleaseContainer = "ReleaseContainer", RemoveContainer = "RemoveContainer", ResetControlPlane = "ResetControlPlane";

        /// <summary>Builds one write object. Call <see cref="Op"/> then the <c>Arg</c> overloads, then <see cref="End"/>.</summary>
        public sealed class OpWriter
        {
            private readonly StringBuilder _sb = new StringBuilder(256);
            private JsonWriter _w;
            /// <summary>A fresh writer per object: the previous one still thinks a comma is due after its closing brace.</summary>
            public OpWriter Op(string name) { _sb.Clear(); _w = new JsonWriter(_sb); _w.BeginObject(); _w.Prop("op", name); return this; }
            public OpWriter Arg(string name, string value) { _w.Prop(name, value ?? ""); return this; }
            public OpWriter Arg(string name, long value) { _w.Prop(name, value); return this; }
            public OpWriter Arg(string name, ulong value) { _w.Prop(name, value); return this; }
            public OpWriter Arg(string name, uint value) { _w.Prop(name, value); return this; }
            public OpWriter Arg(string name, float value) { _w.Key(name); Num(_w, value); return this; }
            public OpWriter Arg(string name, Vector3 value) { _w.Key(name); Vec(_w, value); return this; }
            public string End() { _w.EndObject(); return _sb.ToString(); }
        }

        /// <summary>Wrap a batch of writes in the request body <see cref="ControlPlaneHost"/> reads.</summary>
        public static string WriteBatch(IReadOnlyList<string> ops)
        {
            var sb = new StringBuilder(64 + ops.Count * 128);
            sb.Append("{\"ops\":[");
            for (int i = 0; i < ops.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ops[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Apply every write in <paramref name="body"/> (<c>{"ops":[...]}</c>) to <paramref name="target"/>, in order.
        /// Returns null, or the reason the body was rejected (nothing is applied then).
        /// </summary>
        public static string ApplyBatch(string body, IControlPlane target)
        {
            if (!PersistenceJson.TryParseObject(body, out var root, out string error)) return error;
            if (!root.TryGetValue("ops", out var opsValue) || !(opsValue is List<object> ops)) return "the body must be {\"ops\": [...]}";
            var parsed = new List<Dictionary<string, object>>(ops.Count);
            foreach (var item in ops)
            {
                if (!(item is Dictionary<string, object> o) || !o.ContainsKey("op")) return "every op must be an object with an \"op\" name";
                parsed.Add(o);
            }
            foreach (var o in parsed)
            {
                string reason = Apply(o, target);
                if (reason != null) return reason;
            }
            return null;
        }

        private static string Apply(Dictionary<string, object> o, IControlPlane cp)
        {
            string op = Str(o, "op");
            switch (op)
            {
                case RegisterWorker: cp.RegisterWorker(Str(o, "workerId"), (uint)Num(o, "workerIndex"), Str(o, "address"), (ushort)Num(o, "port")); return null;
                case HeartbeatWorker:
                {
                    var stats = new WorkerStats
                    {
                        TickCount = (ulong)Num(o, "tickCount"),
                        TickMs = (float)Num(o, "tickMs"),
                        EntityCount = (uint)Num(o, "entityCount"),
                        AuthoritativeCount = (uint)Num(o, "authoritativeCount"),
                        GhostCount = (uint)Num(o, "ghostCount"),
                        PlayerCount = (uint)Num(o, "playerCount"),
                        BotCount = (uint)Num(o, "botCount"),
                        ServerDrivenCount = (uint)Num(o, "serverDrivenCount"),
                    };
                    cp.HeartbeatWorker(Str(o, "workerId"), Str(o, "status"), stats);
                    return null;
                }
                case UnregisterWorker: cp.UnregisterWorker(Str(o, "workerId")); return null;
                case RegisterGateway: cp.RegisterGateway(Str(o, "gatewayId"), Str(o, "address"), (ushort)Num(o, "port")); return null;
                case HeartbeatGateway: cp.HeartbeatGateway(Str(o, "gatewayId")); return null;
                case UnregisterGateway: cp.UnregisterGateway(Str(o, "gatewayId")); return null;
                case HeartbeatOrchestrator: cp.HeartbeatOrchestrator(Str(o, "orchestratorId"), (uint)Num(o, "desiredWorkers")); return null;
                case SetSetting: cp.SetSetting(Str(o, "key"), Str(o, "value")); return null;
                case EnsureContainer: cp.EnsureContainer(Str(o, "containerId")); return null;
                case EnsureRuntimeContainer: cp.EnsureRuntimeContainer(Str(o, "containerId"), new Bounds(Vec(o, "center"), Vec(o, "size")), Str(o, "workerId")); return null;
                case TouchContainer: cp.TouchContainer(Str(o, "containerId")); return null;
                case AssignContainer: cp.AssignContainer(Str(o, "containerId"), Str(o, "workerId")); return null;
                case PinContainer: cp.PinContainer(Str(o, "containerId"), Str(o, "workerId")); return null;
                case SetLeaseState: cp.SetLeaseState(Str(o, "containerId"), Str(o, "state")); return null;
                case ReleaseContainer: cp.ReleaseContainer(Str(o, "containerId")); return null;
                case RemoveContainer: cp.RemoveContainer(Str(o, "containerId")); return null;
                case ResetControlPlane: cp.ResetControlPlane(); return null;
                default: return $"unknown control-plane op '{op}'";
            }
        }

        // ---------------------------------------------------------------------------------------- helpers

        private static readonly DateTime Epoch1970 = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long ToUnixMs(DateTime t)
        {
            if (t == default) return 0;
            return (long)(t.ToUniversalTime() - Epoch1970).TotalMilliseconds;
        }

        public static DateTime FromUnixMs(double ms) => Epoch1970.AddMilliseconds(ms);

        /// <summary>A float at full precision (<see cref="JsonWriter.Value(double)"/> rounds to three decimals).</summary>
        public static void Num(JsonWriter w, float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) w.Raw("null");
            else w.Raw(f.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void Vec(JsonWriter w, Vector3 v)
        {
            w.BeginArray();
            Num(w, v.x); Num(w, v.y); Num(w, v.z);
            w.EndArray();
        }

        public static string Str(Dictionary<string, object> o, string key) => PersistenceJson.GetString(o, key);

        public static double Num(Dictionary<string, object> o, string key)
        {
            if (o == null || !o.TryGetValue(key, out var v) || v == null) return 0;
            if (v is double d) return d;
            if (v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)) return p;
            if (v is bool b) return b ? 1 : 0;
            return 0;
        }

        public static bool Bool(Dictionary<string, object> o, string key)
        {
            if (o == null || !o.TryGetValue(key, out var v) || v == null) return false;
            if (v is bool b) return b;
            if (v is double d) return d != 0;
            if (v is string s) return s == "true" || s == "1";
            return false;
        }

        public static Vector3 Vec(Dictionary<string, object> o, string key)
        {
            if (o != null && o.TryGetValue(key, out var v) && PersistenceJson.TryNumbers(v, 3, out var n)) return new Vector3((float)n[0], (float)n[1], (float)n[2]);
            return Vector3.zero;
        }
    }
}
