using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Nebula.World;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula
{
    /// <summary>
    /// The data behind the World map page of the Nebula Dashboard (<c>/map</c>). It has two halves:
    /// <list type="bullet">
    /// <item><b>Geometry</b>, built once on the orchestrator's main thread from <see cref="ContainerRegistry"/> and the
    /// world definition: every static container's box in absolute world coordinates, its cell, the container that
    /// encloses it and its neighbours (<c>GET /api/map/geometry</c>).</item>
    /// <item><b>Telemetry</b>, the latest document each worker posted (<see cref="WorkerTelemetry"/>,
    /// <c>POST /api/telemetry</c>): entity counts per container, the pose of every carried container whose carrier it
    /// simulates, the cells it has loaded and, while somebody has the map open, its authoritative entities
    /// (<c>GET /api/map</c>).</item>
    /// </list>
    /// Thread-safe: workers post, and the page reads, on the dashboard's listener threads. Worker documents are kept
    /// as received and spliced into the map document unparsed (the runtime carries no JSON reader); the page does the
    /// aggregation.
    /// </summary>
    public sealed class MeshTelemetry
    {
        /// <summary>Largest worker document accepted, in characters.</summary>
        public const int MaxDocumentChars = 4 * 1024 * 1024;
        /// <summary>Workers are asked for their entities while the map was read within this many seconds.</summary>
        public const double DetailWindowSeconds = 5.0;
        /// <summary>A worker document older than this is dropped: its worker died, retired or stopped posting.</summary>
        public const double ExpireSeconds = 15.0;

        private sealed class Document
        {
            public string Json;
            public double ReceivedAt;
        }

        private static readonly Regex WorkerIdPattern = new Regex("^\\s*\\{\\s*\"worker\"\\s*:\\s*\"([A-Za-z0-9_.:-]{1,64})\"", RegexOptions.CultureInvariant);
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly object _lock = new object();
        private readonly SortedDictionary<string, Document> _documents = new SortedDictionary<string, Document>(StringComparer.Ordinal);
        /// <summary>The latest per-container counts, by container id, from whichever worker reported each container last.</summary>
        private readonly Dictionary<string, ContainerLoad> _occupancy = new Dictionary<string, ContainerLoad>(StringComparer.Ordinal);
        /// <summary>The latest interest summary per worker, from the same documents.</summary>
        private readonly Dictionary<string, WorkerInterest> _interest = new Dictionary<string, WorkerInterest>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<string, ContainerLoad>> _parsed = new List<KeyValuePair<string, ContainerLoad>>();
        private readonly List<string> _expired = new List<string>();
        private readonly StringBuilder _sb = new StringBuilder(1 << 16);
        private readonly Func<double> _now;
        private string _geometry = "{\"partitioned\":false,\"containers\":[]}";
        private int _geometryVersion;
        private double _lastMapRead = double.NegativeInfinity;

        /// <param name="now">Seconds on a monotonic clock; a stopwatch by default.</param>
        public MeshTelemetry(Func<double> now = null)
        {
            _now = now ?? (() => Clock.Elapsed.TotalSeconds);
        }

        /// <summary>Somebody read the map within <see cref="DetailWindowSeconds"/>, so workers should include their entities.</summary>
        public bool DetailWanted
        {
            get { lock (_lock) return _now() - _lastMapRead <= DetailWindowSeconds; }
        }

        /// <summary>Bumped by every <see cref="PublishGeometry"/>; the page refetches the geometry when it changes.</summary>
        public int GeometryVersion
        {
            get { lock (_lock) return _geometryVersion; }
        }

        /// <summary>The static geometry document served at <c>/api/map/geometry</c>.</summary>
        public string GeometryJson
        {
            get { lock (_lock) return _geometry; }
        }

        /// <summary>Workers with a document on file (expired ones are only dropped when the map is read).</summary>
        public int WorkerCount
        {
            get { lock (_lock) return _documents.Count; }
        }

        /// <summary>Replace the static geometry document (see <see cref="BuildGeometryJson"/>).</summary>
        public void PublishGeometry(string json)
        {
            lock (_lock)
            {
                _geometry = string.IsNullOrEmpty(json) ? "{\"partitioned\":false,\"containers\":[]}" : json;
                _geometryVersion++;
            }
        }

        /// <summary>
        /// Store a worker's telemetry document, replacing its previous one. The document must be a JSON object whose
        /// first property is <c>"worker"</c>, as <see cref="WorkerTelemetry"/> writes it. Returns null when it was
        /// stored, otherwise why it was refused. <paramref name="detail"/> is the answer for the worker: whether its
        /// next document should include entities.
        /// </summary>
        public string Accept(string json, out bool detail)
        {
            detail = DetailWanted;
            if (string.IsNullOrEmpty(json)) return "empty telemetry document";
            if (json.Length > MaxDocumentChars) return $"telemetry document larger than {MaxDocumentChars} characters";
            var m = WorkerIdPattern.Match(json);
            if (!m.Success) return "telemetry must be a JSON object whose first property is \"worker\"";
            int end = json.Length - 1;
            while (end > 0 && char.IsWhiteSpace(json[end])) end--;
            if (json[end] != '}') return "telemetry document is not a JSON object";
            string workerId = m.Groups[1].Value;
            lock (_lock)
            {
                double now = _now();
                _documents[workerId] = new Document { Json = json, ReceivedAt = now };
                _parsed.Clear();
                ParseContainers(json, _parsed);
                _expired.Clear();
                foreach (var kv in _occupancy) if (kv.Value.WorkerId == workerId) _expired.Add(kv.Key);
                foreach (var id in _expired) _occupancy.Remove(id);
                for (int i = 0; i < _parsed.Count; i++)
                {
                    var load = _parsed[i].Value;
                    load.WorkerId = workerId;
                    load.ReceivedAt = now;
                    _occupancy[_parsed[i].Key] = load;
                }
                if (ParseInterest(json, out var interest))
                {
                    interest.ReceivedAt = now;
                    _interest[workerId] = interest;
                }
                else _interest.Remove(workerId);
            }
            return null;
        }

        /// <summary>
        /// Snapshot the latest per-container counts into <paramref name="result"/> (cleared first), dropping reports
        /// older than <see cref="ExpireSeconds"/>. Containers nobody reported are absent. This is what the
        /// cost-aware assignment policy reads once per pass.
        /// </summary>
        public void CopyOccupancy(Dictionary<string, ContainerLoad> result)
        {
            result.Clear();
            lock (_lock)
            {
                double now = _now();
                foreach (var kv in _occupancy) if (now - kv.Value.ReceivedAt <= ExpireSeconds) result[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// Pull the per-container counts out of a worker document without parsing the rest of it (the entity list
        /// can be megabytes). The array looks like <c>"containers":[{"id":"arena","players":1,"bots":0,...},...]</c>
        /// as <see cref="WorkerTelemetry"/> writes it; the slot for entities in no container ("") is skipped.
        /// </summary>
        public static void ParseContainers(string json, List<KeyValuePair<string, ContainerLoad>> result)
        {
            int at = json.IndexOf("\"containers\"", StringComparison.Ordinal);
            if (at < 0) return;
            at = json.IndexOf('[', at);
            if (at < 0) return;
            at++;
            while (at < json.Length)
            {
                while (at < json.Length && (char.IsWhiteSpace(json[at]) || json[at] == ',')) at++;
                if (at >= json.Length || json[at] != '{') return;
                at++;
                string id = null;
                var load = new ContainerLoad();
                while (at < json.Length)
                {
                    while (at < json.Length && (char.IsWhiteSpace(json[at]) || json[at] == ',')) at++;
                    if (at >= json.Length) return;
                    if (json[at] == '}') { at++; break; }
                    if (json[at] != '"') return;
                    int keyEnd = json.IndexOf('"', at + 1);
                    if (keyEnd < 0) return;
                    string key = json.Substring(at + 1, keyEnd - at - 1);
                    at = json.IndexOf(':', keyEnd);
                    if (at < 0) return;
                    at++;
                    while (at < json.Length && char.IsWhiteSpace(json[at])) at++;
                    if (at >= json.Length) return;
                    if (json[at] == '"')
                    {
                        int strEnd = at + 1;
                        while (strEnd < json.Length && json[strEnd] != '"') { if (json[strEnd] == '\\') strEnd++; strEnd++; }
                        if (strEnd >= json.Length) return;
                        if (key == "id") id = json.Substring(at + 1, strEnd - at - 1);
                        at = strEnd + 1;
                    }
                    else
                    {
                        int numEnd = at;
                        while (numEnd < json.Length && (char.IsDigit(json[numEnd]) || json[numEnd] == '-' || json[numEnd] == '.' || json[numEnd] == 'e' || json[numEnd] == 'E' || json[numEnd] == '+')) numEnd++;
                        if (numEnd == at) return;
                        int.TryParse(json.Substring(at, numEnd - at), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value);
                        switch (key)
                        {
                            case "players": load.Players = value; break;
                            case "bots": load.Bots = value; break;
                            case "serverDriven": load.ServerDriven = value; break;
                            case "other": load.Other = value; break;
                            case "ghosts": load.Ghosts = value; break;
                        }
                        at = numEnd;
                    }
                }
                if (!string.IsNullOrEmpty(id)) result.Add(new KeyValuePair<string, ContainerLoad>(id, load));
                while (at < json.Length && char.IsWhiteSpace(json[at])) at++;
                if (at < json.Length && json[at] == ']') return;
            }
        }

        /// <summary>
        /// The interest summary of one worker's latest document: how much of what it holds it actually sends,
        /// what that costs, and whether it is asking for the world to be partitioned.
        /// </summary>
        public struct WorkerInterest
        {
            public int Regions;
            public int Gateways;
            public double FilterMs;
            public bool Global;
            public long EntriesSent, EntriesTotal, BytesSent, BytesUnfiltered;
            public string Warning;
            public double ReceivedAt;
        }

        /// <summary>
        /// Snapshot each worker's interest summary into <paramref name="result"/> (cleared first), dropping reports
        /// older than <see cref="ExpireSeconds"/>. This is what the dashboard's worker rows read.
        /// </summary>
        public void CopyInterest(Dictionary<string, WorkerInterest> result)
        {
            result.Clear();
            lock (_lock)
            {
                double now = _now();
                foreach (var kv in _interest) if (now - kv.Value.ReceivedAt <= ExpireSeconds) result[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// Pull the <c>"interest"</c> object out of a worker document without parsing the rest of it. Deliberately
        /// narrow: it reads the flat numeric and string properties of that one object and stops at its nested
        /// <c>"gateways"</c> array, which only the map page needs.
        /// </summary>
        public static bool ParseInterest(string json, out WorkerInterest interest)
        {
            interest = new WorkerInterest { Warning = "" };
            int at = json.IndexOf("\"interest\"", StringComparison.Ordinal);
            if (at < 0) return false;
            at = json.IndexOf('{', at);
            if (at < 0) return false;
            at++;
            while (at < json.Length)
            {
                while (at < json.Length && (char.IsWhiteSpace(json[at]) || json[at] == ',')) at++;
                if (at >= json.Length || json[at] == '}') return true;
                if (json[at] != '"') return true;
                int keyEnd = json.IndexOf('"', at + 1);
                if (keyEnd < 0) return true;
                string key = json.Substring(at + 1, keyEnd - at - 1);
                at = json.IndexOf(':', keyEnd);
                if (at < 0) return true;
                at++;
                while (at < json.Length && char.IsWhiteSpace(json[at])) at++;
                if (at >= json.Length) return true;
                char c = json[at];
                if (c == '[')
                {
                    // The per-gateway rows: counted here, read in full only by the map page.
                    int depth = 0;
                    int count = 0;
                    for (; at < json.Length; at++)
                    {
                        if (json[at] == '[') depth++;
                        else if (json[at] == ']') { depth--; if (depth == 0) { at++; break; } }
                        else if (json[at] == '{' && depth == 1) count++;
                    }
                    if (key == "gateways") interest.Gateways = count;
                }
                else if (c == '"')
                {
                    int strEnd = at + 1;
                    while (strEnd < json.Length && json[strEnd] != '"') { if (json[strEnd] == '\\') strEnd++; strEnd++; }
                    if (strEnd >= json.Length) return true;
                    if (key == "warning") interest.Warning = json.Substring(at + 1, strEnd - at - 1);
                    at = strEnd + 1;
                }
                else if (c == 't' || c == 'f')
                {
                    bool value = c == 't';
                    while (at < json.Length && char.IsLetter(json[at])) at++;
                    if (key == "global") interest.Global = value;
                }
                else
                {
                    int numEnd = at;
                    while (numEnd < json.Length && (char.IsDigit(json[numEnd]) || json[numEnd] == '-' || json[numEnd] == '.' || json[numEnd] == 'e' || json[numEnd] == 'E' || json[numEnd] == '+')) numEnd++;
                    if (numEnd == at) return true;
                    double.TryParse(json.Substring(at, numEnd - at), NumberStyles.Float, CultureInfo.InvariantCulture, out double value);
                    switch (key)
                    {
                        case "regions": interest.Regions = (int)value; break;
                        case "filterMs": interest.FilterMs = value; break;
                        case "entriesSent": interest.EntriesSent = (long)value; break;
                        case "entriesTotal": interest.EntriesTotal = (long)value; break;
                        case "bytesSent": interest.BytesSent = (long)value; break;
                        case "bytesUnfiltered": interest.BytesUnfiltered = (long)value; break;
                    }
                    at = numEnd;
                }
            }
            return true;
        }

        /// <summary>Drop a worker's document now instead of waiting for it to expire.</summary>
        public void Forget(string workerId)
        {
            if (workerId == null) return;
            lock (_lock)
            {
                _documents.Remove(workerId);
                _interest.Remove(workerId);
                _expired.Clear();
                foreach (var kv in _occupancy) if (kv.Value.WorkerId == workerId) _expired.Add(kv.Key);
                foreach (var id in _expired) _occupancy.Remove(id);
            }
        }

        /// <summary>
        /// The live map document: every worker's latest telemetry, with its age in seconds, sorted by worker id.
        /// Reading it marks the map as watched, which asks workers for their entities (<see cref="DetailWanted"/>).
        /// </summary>
        public string BuildMapJson()
        {
            lock (_lock)
            {
                double now = _now();
                _lastMapRead = now;
                _expired.Clear();
                foreach (var kv in _documents) if (now - kv.Value.ReceivedAt > ExpireSeconds) _expired.Add(kv.Key);
                foreach (var id in _expired) _documents.Remove(id);

                _sb.Clear();
                var w = new JsonWriter(_sb);
                w.BeginObject();
                w.Prop("serverTimeUtc", DateTime.UtcNow.ToString("o"));
                w.Prop("geometryVersion", _geometryVersion);
                w.Key("workers");
                w.BeginArray();
                foreach (var kv in _documents)
                {
                    w.BeginObject();
                    w.Prop("id", kv.Key);
                    w.Prop("ageSeconds", now - kv.Value.ReceivedAt);
                    w.Key("doc");
                    w.Raw(kv.Value.Json);
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
                return _sb.ToString();
            }
        }

        // ---------------------------------------------------------------------------------------- geometry

        /// <summary>
        /// Every container of the loaded level as the map draws it: its box in absolute world coordinates
        /// (<see cref="ToAbsolute(Vector3, out double, out double, out double)"/>), its cell when it belongs to an
        /// authored partition, the smallest container enclosing it and its static neighbours, plus the world's cells
        /// and the ghost band and hysteresis the mesh runs with. A runtime-only world is partitioned even though its
        /// authored container grid is empty. Main thread.
        /// </summary>
        public static string BuildGeometryJson(NebulaConfig config)
        {
            var sb = new StringBuilder(4096);
            var w = new JsonWriter(sb);
            var world = WorldOrigin.Definition;
            bool partitioned = world != null;
            bool gridded = ContainerRegistry.IsGridded && partitioned;
            w.BeginObject();
            w.Prop("partitioned", partitioned);
            if (partitioned)
            {
                w.Key("world");
                w.BeginObject();
                w.Prop("name", world.WorldName);
                WriteVector(w, "cellSize", world.CellSize.x, world.CellSize.y, world.CellSize.z);
                w.Key("cells");
                w.BeginArray();
                foreach (var cell in world.Cells)
                {
                    w.BeginObject();
                    WriteCell(w, "coord", cell.Coord);
                    w.Prop("scene", cell.SceneName);
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.Prop("ghostBandMargin", config != null ? config.GhostBandMargin : 0f);
            w.Prop("handoverHysteresis", config != null ? config.HandoverHysteresis : 0f);
            w.Key("containers");
            w.BeginArray();
            foreach (var c in ContainerRegistry.All) WriteContainer(w, c, gridded);
            foreach (var c in ContainerRegistry.Runtime) WriteContainer(w, c, gridded);
            w.EndArray();
            w.EndObject();
            return sb.ToString();
        }

        private static void WriteContainer(JsonWriter w, Container c, bool gridded)
        {
            w.BeginObject();
            w.Prop("id", c.ContainerId);
            w.Prop("index", (int)c.Index);
            if (c.IsRuntime) w.Prop("runtime", true);
            if (gridded && !c.IsRuntime)
            {
                WriteCell(w, "cell", c.Cell);
                w.Prop("isCell", c.IsCell);
            }
            WriteBox(w, c);
            var parent = EnclosingStatic(c);
            w.Prop("parent", parent != null ? parent.ContainerId : "");
            w.Key("neighbors");
            w.BeginArray();
            foreach (var n in c.Neighbors) w.Value(n.ContainerId);
            w.EndArray();
            w.EndObject();
        }

        /// <summary>The smallest static container whose box holds <paramref name="c"/>'s, or null. In a partitioned world only <paramref name="c"/>'s own cell is searched.</summary>
        public static Container EnclosingStatic(Container c)
        {
            if (c == null || c.IsDynamic || c.IsRuntime) return null;
            var candidates = ContainerRegistry.IsGridded ? ContainerRegistry.InCell(c.Cell) : ContainerRegistry.All;
            Container best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                var o = candidates[i];
                if (o == c || !o.Encloses(c)) continue;
                if (best == null || o.Volume < best.Volume) best = o;
            }
            return best;
        }

        /// <summary>
        /// Absolute world position of a position in this process's frame. Without a partitioned world the frame is
        /// the world. With one, every process keeps its own floating origin (<see cref="WorldOrigin"/>), so the origin
        /// cell's offset is added back: two workers report the same place with the same numbers.
        /// </summary>
        public static void ToAbsolute(Vector3 frame, out double x, out double y, out double z)
        {
            var world = WorldOrigin.Definition;
            if (world == null)
            {
                x = frame.x; y = frame.y; z = frame.z;
                return;
            }
            ToAbsolute(frame, WorldOrigin.Cell, world.CellSize, out x, out y, out z);
        }

        /// <summary>Absolute world position of <paramref name="frame"/> in the frame whose origin cell is <paramref name="originCell"/>.</summary>
        public static void ToAbsolute(Vector3 frame, Vector3Int originCell, Vector3 cellSize, out double x, out double y, out double z)
        {
            x = (double)originCell.x * cellSize.x + frame.x;
            y = (double)originCell.y * cellSize.y + frame.y;
            z = (double)originCell.z * cellSize.z + frame.z;
        }

        /// <summary>A container's box: <c>center</c> (absolute), <c>size</c> (scaled) and <c>rotation</c> (quaternion x, y, z, w).</summary>
        internal static void WriteBox(JsonWriter w, Container c)
        {
            var center = c.ToWorld(c.Center);
            ToAbsolute(center, out double x, out double y, out double z);
            WriteVector(w, "center", x, y, z);
            var scale = c.transform.lossyScale;
            WriteVector(w, "size", Mathf.Abs(c.Size.x * scale.x), Mathf.Abs(c.Size.y * scale.y), Mathf.Abs(c.Size.z * scale.z));
            var r = c.Rotation;
            w.Key("rotation");
            w.BeginArray();
            w.Value((double)r.x);
            w.Value((double)r.y);
            w.Value((double)r.z);
            w.Value((double)r.w);
            w.EndArray();
        }

        internal static void WriteVector(JsonWriter w, string key, double x, double y, double z)
        {
            w.Key(key);
            w.BeginArray();
            w.Value(Math.Round(x, 2));
            w.Value(Math.Round(y, 2));
            w.Value(Math.Round(z, 2));
            w.EndArray();
        }

        internal static void WriteCell(JsonWriter w, string key, Vector3Int cell)
        {
            w.Key(key);
            w.BeginArray();
            w.Value((long)cell.x);
            w.Value((long)cell.y);
            w.Value((long)cell.z);
            w.EndArray();
        }
    }
}
