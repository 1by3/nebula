using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>
    /// A crowd of server-owned entities on the in-process worker mesh (<see cref="ConformanceMesh"/>): two real
    /// workers, each leasing one 256 m container either side of a seam at x = 0, one gateway linked to both and
    /// subscribed to every region, and any number of walkers spawned server-driven. It exists to measure what a
    /// plain server-owned entity costs (<c>docs/server-owned-entities.md</c>) and to run the crowd scenarios that the
    /// relevance-tier and dormancy tests share, so both see exactly the same world.
    /// <para>
    /// A tick here is the worker's own <c>Tick</c>, run by reflection, then <see cref="ConformanceMesh.Pump"/>, which
    /// delivers what each worker sent into the other's <c>Dispatch</c> (ghost state, handovers) and records what the
    /// gateway was sent. Nothing is a test double except the transport.
    /// </para>
    /// </summary>
    public sealed class CrowdMesh : IDisposable
    {
        /// <summary>A walker: moves at a constant velocity inside a box, bouncing off its edges. Carries both across a handover.</summary>
        public sealed class Walker : NetworkBehaviour
        {
            public Vector3 Velocity;
            public Vector2 Min = new Vector2(-250f, -250f);
            public Vector2 Max = new Vector2(250f, 250f);
            public int Ticks;

            public override void NetworkTick(uint tick, float deltaTime)
            {
                Ticks++;
                if (Velocity == Vector3.zero) return;
                var p = transform.position + Velocity * deltaTime;
                if (p.x < Min.x || p.x > Max.x) { Velocity.x = -Velocity.x; p.x = Mathf.Clamp(p.x, Min.x, Max.x); }
                if (p.z < Min.y || p.z > Max.y) { Velocity.z = -Velocity.z; p.z = Mathf.Clamp(p.z, Min.y, Max.y); }
                transform.SetPositionAndRotation(p, Quaternion.LookRotation(Velocity));
                Identity.Motion.Velocity = Velocity;
            }

            public override void WriteHandoverState(NetworkWriter writer)
            {
                writer.WriteVector3(Velocity);
                writer.WriteFloat(Min.x); writer.WriteFloat(Min.y);
                writer.WriteFloat(Max.x); writer.WriteFloat(Max.y);
            }

            public override void ReadHandoverState(NetworkReader reader)
            {
                Velocity = reader.ReadVector3();
                Min = new Vector2(reader.ReadFloat(), reader.ReadFloat());
                Max = new Vector2(reader.ReadFloat(), reader.ReadFloat());
            }
        }

        /// <summary>What one measured window cost, per worker and on the wire.</summary>
        public sealed class Window
        {
            public int Ticks;
            public readonly double[] AvgMs = new double[2];
            public readonly double[] P95Ms = new double[2];
            public readonly double[] MaxMs = new double[2];
            /// <summary>Wall time of delivering the tick's traffic into the receiving workers' Dispatch, per tick.</summary>
            public double PumpMs;
            /// <summary>Bytes per second from each worker to the gateway, and from each worker to the other.</summary>
            public readonly double[] ToGatewayBytesPerSecond = new double[2];
            public readonly double[] ToPeerBytesPerSecond = new double[2];
            /// <summary>World-state entries per second sent to the gateway (both workers), and how many of them were reliable.</summary>
            public double EntriesPerSecond, ReliableEntriesPerSecond;
            /// <summary>Spawn, forget and despawn messages per second sent to the gateway (both workers).</summary>
            public double SpawnsPerSecond, ForgetsPerSecond;
            /// <summary>Authority handovers per second, both directions.</summary>
            public double HandoversPerSecond;
            public int GhostsHeld;
            public long AllocatedBytesPerTick;
            public string Sections = "";
            public readonly Dictionary<MsgId, long> GatewayBytesById = new Dictionary<MsgId, long>();

            public double ToGatewayBytesPerSecondTotal => ToGatewayBytesPerSecond[0] + ToGatewayBytesPerSecond[1];
        }

        public const float HalfWidth = 256f;

        public readonly ConformanceMesh Mesh;
        public readonly Container West, East;
        public readonly ConformanceMesh.Gateway Gateway;
        public readonly ushort WalkerPrefab;
        public readonly List<NetworkIdentity> Spawned = new List<NetworkIdentity>();
        private readonly System.Random _random;
        private uint _tick;

        public ConformanceMesh.Worker W1 => Mesh[0];
        public ConformanceMesh.Worker W2 => Mesh[1];
        public uint CurrentTick => _tick;

        public CrowdMesh(int seed = 359, Action<NetworkIdentity> configurePrefab = null)
        {
            _random = new System.Random(seed);
            Mesh = new ConformanceMesh(2);
            West = Mesh.AddStaticContainer("crowd-west", new Vector3(-HalfWidth / 2, 0, 0), new Vector3(HalfWidth, 60, 2 * HalfWidth));
            East = Mesh.AddStaticContainer("crowd-east", new Vector3(HalfWidth / 2, 0, 0), new Vector3(HalfWidth, 60, 2 * HalfWidth));
            Mesh.SetOwner(West, W1);
            Mesh.SetOwner(East, W2);

            var prefab = new GameObject("crowd-walker");
            var identity = prefab.AddComponent<NetworkIdentity>();
            prefab.AddComponent<NetworkTransform>();
            prefab.AddComponent<Walker>();
            configurePrefab?.Invoke(identity);
            WalkerPrefab = Mesh.RegisterPrefab(prefab);

            Gateway = Mesh.AddGateway("gw1");
            Mesh.LinkGateway(Gateway);
            SubscribeEverything();
        }

        /// <summary>The gateway asks both workers for every region of the two containers, as clients standing everywhere would.</summary>
        private void SubscribeEverything()
        {
            var grid = InterestGrid.Resolve(InterestSettings.Default);
            var set = new HashSet<ulong>();
            for (float x = -HalfWidth + 1; x < HalfWidth; x += 16f)
                for (float z = -HalfWidth + 1; z < HalfWidth; z += 16f)
                    set.Add(grid.RegionOf(x, 0, z));
            var regions = new List<ulong>(set);
            foreach (var w in Mesh.Workers)
                Mesh.FromGateway(Gateway, w, writer => new InterestSubscribeMsg
                {
                    Seq = 1, Grid = grid,
                    Flags = InterestSubscribeFlags.Full | InterestSubscribeFlags.Commit,
                    Add = regions, Remove = new List<ulong>(), FociRegions = new List<ulong>(), Entities = new List<ulong>(),
                    SetCount = (uint)regions.Count, SetHash = RegionSubscription.Hash(regions),
                }.Write(writer));
        }

        /// <summary>
        /// Spawn <paramref name="count"/> walkers on <paramref name="worker"/> inside its container, at random positions
        /// in the x range given, walking at <paramref name="speed"/> m/s in a random direction (0 = standing still).
        /// </summary>
        public List<NetworkIdentity> SpawnWalkers(ConformanceMesh.Worker worker, int count, float minX, float maxX, float speed, Vector2? boxMin = null, Vector2? boxMax = null)
        {
            var container = worker == W1 ? West : East;
            var made = new List<NetworkIdentity>(count);
            for (int i = 0; i < count; i++)
            {
                float x = Mathf.Lerp(minX, maxX, (float)_random.NextDouble());
                float z = Mathf.Lerp(-HalfWidth + 8, HalfWidth - 8, (float)_random.NextDouble());
                float angle = (float)(_random.NextDouble() * Math.PI * 2);
                var velocity = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * speed;
                var e = worker.SpawnServerDriven(WalkerPrefab, container, new Vector3(x, 0, z), velocity == Vector3.zero ? Quaternion.identity : Quaternion.LookRotation(velocity));
                var walker = e.GetComponent<Walker>();
                walker.Velocity = velocity;
                if (boxMin.HasValue) walker.Min = boxMin.Value;
                if (boxMax.HasValue) walker.Max = boxMax.Value;
                e.Motion.Velocity = velocity;
                made.Add(e);
                Spawned.Add(e);
            }
            return made;
        }

        /// <summary>
        /// Run <paramref name="ticks"/> ticks of both workers without measuring (spawns settle, ghosts form). What was
        /// delivered is dropped after each tick unless <paramref name="keepDelivered"/>, when the caller reads and
        /// clears <see cref="ConformanceMesh.Delivered"/> itself.
        /// </summary>
        public void Run(int ticks, bool keepDelivered = false)
        {
            for (int i = 0; i < ticks; i++)
            {
                _tick++;
                W1.Tick(_tick);
                W2.Tick(_tick);
                Mesh.Pump();
                if (!keepDelivered) Mesh.Delivered.Clear();
            }
        }

        /// <summary>Run <paramref name="ticks"/> ticks and measure each worker's tick, the delivery, and every byte sent.</summary>
        public Window Measure(int ticks)
        {
            var window = new Window { Ticks = ticks };
            var samples = new[] { new List<double>(ticks), new List<double>(ticks) };
            var watch = new Stopwatch();
            double pump = 0;
            long toGateway0 = 0, toGateway1 = 0, toPeer0 = 0, toPeer1 = 0, entries = 0, reliable = 0, spawns = 0, forgets = 0, handovers = 0;
            NebulaProfiler.ReportAndReset(1);
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < ticks; i++)
            {
                _tick++;
                watch.Restart(); W1.Tick(_tick); watch.Stop(); samples[0].Add(watch.Elapsed.TotalMilliseconds);
                watch.Restart(); W2.Tick(_tick); watch.Stop(); samples[1].Add(watch.Elapsed.TotalMilliseconds);
                watch.Restart(); Mesh.Pump(); watch.Stop(); pump += watch.Elapsed.TotalMilliseconds;
                foreach (var m in Mesh.Delivered)
                {
                    bool fromW1 = m.From == W1.Id;
                    if (m.To == Gateway.Id)
                    {
                        if (fromW1) toGateway0 += m.Bytes.Length; else toGateway1 += m.Bytes.Length;
                        window.GatewayBytesById.TryGetValue(m.Id, out long had);
                        window.GatewayBytesById[m.Id] = had + m.Bytes.Length;
                        if (m.Id == MsgId.WorldState) CountEntries(m.Bytes, ref entries, ref reliable);
                        else if (m.Id == MsgId.EntitySpawn) spawns++;
                        else if (m.Id == MsgId.EntityForget) forgets++;
                    }
                    else if (m.From == W1.Id || m.From == W2.Id)
                    {
                        if (fromW1) toPeer0 += m.Bytes.Length; else toPeer1 += m.Bytes.Length;
                        if (m.Id == MsgId.AuthorityTransfer) handovers++;
                    }
                }
                Mesh.Delivered.Clear();
            }
            window.AllocatedBytesPerTick = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / Math.Max(1, ticks);
            window.Sections = NebulaProfiler.ReportAndReset(ticks);
            double seconds = ticks * NetworkTime.TickInterval;
            for (int w = 0; w < 2; w++)
            {
                var s = samples[w];
                double sum = 0, max = 0;
                foreach (var v in s) { sum += v; if (v > max) max = v; }
                s.Sort();
                window.AvgMs[w] = sum / Math.Max(1, s.Count);
                window.P95Ms[w] = s.Count > 0 ? s[Math.Min(s.Count - 1, (int)(s.Count * 0.95))] : 0;
                window.MaxMs[w] = max;
            }
            window.PumpMs = pump / Math.Max(1, ticks);
            window.ToGatewayBytesPerSecond[0] = toGateway0 / seconds;
            window.ToGatewayBytesPerSecond[1] = toGateway1 / seconds;
            window.ToPeerBytesPerSecond[0] = toPeer0 / seconds;
            window.ToPeerBytesPerSecond[1] = toPeer1 / seconds;
            window.EntriesPerSecond = entries / seconds;
            window.ReliableEntriesPerSecond = reliable / seconds;
            window.SpawnsPerSecond = spawns / seconds;
            window.ForgetsPerSecond = forgets / seconds;
            window.HandoversPerSecond = handovers / seconds;
            window.GhostsHeld = W1.Instance.GhostsHeld + W2.Instance.GhostsHeld;
            return window;
        }

        private static void CountEntries(byte[] bytes, ref long entries, ref long reliable)
        {
            var r = new NetworkReader(bytes);
            r.ReadByte();
            WorldStateMsg.ReadHeader(r, out _, out _, out ushort count);
            for (int i = 0; i < count; i++)
            {
                var entry = EntityStateEntry.Read(r);
                entries++;
                if (entry.Reliable) reliable++;
            }
        }

        /// <summary>Entities each worker holds with authority right now.</summary>
        public int AuthoritativeOn(ConformanceMesh.Worker worker)
        {
            int n = 0;
            foreach (var e in worker.Instance.Entities) if (e != null && e.HasAuthority) n++;
            return n;
        }

        public void Dispose() => Mesh.Dispose();

        // ------------------------------------------------------------------------------------------- reporting

        /// <summary>Where the scale artifacts go: <c>Logs/scale/</c> at the repository (Unity project) root.</summary>
        public static string ArtifactPath(string name)
        {
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, ".."));
            string dir = System.IO.Path.Combine(root, "Logs", "scale");
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, name);
        }

        public const string CsvHeader = "layer,scenario,entities,ticks,w1_avg_ms,w1_p95_ms,w1_max_ms,w2_avg_ms,w2_p95_ms,pump_ms,w1_to_gateway_Bps,w2_to_gateway_Bps,w1_to_w2_Bps,w2_to_w1_Bps,entries_per_s,reliable_entries_per_s,spawns_per_s,forgets_per_s,handovers_per_s,ghosts,alloc_bytes_per_tick,sections";

        public static string CsvRow(string scenario, int entities, Window w)
        {
            var sb = new StringBuilder();
            sb.Append("editor,").Append(scenario).Append(',').Append(entities).Append(',').Append(w.Ticks).Append(',');
            sb.Append(F(w.AvgMs[0])).Append(',').Append(F(w.P95Ms[0])).Append(',').Append(F(w.MaxMs[0])).Append(',');
            sb.Append(F(w.AvgMs[1])).Append(',').Append(F(w.P95Ms[1])).Append(',').Append(F(w.PumpMs)).Append(',');
            sb.Append(F0(w.ToGatewayBytesPerSecond[0])).Append(',').Append(F0(w.ToGatewayBytesPerSecond[1])).Append(',');
            sb.Append(F0(w.ToPeerBytesPerSecond[0])).Append(',').Append(F0(w.ToPeerBytesPerSecond[1])).Append(',');
            sb.Append(F0(w.EntriesPerSecond)).Append(',').Append(F0(w.ReliableEntriesPerSecond)).Append(',');
            sb.Append(F(w.SpawnsPerSecond)).Append(',').Append(F(w.ForgetsPerSecond)).Append(',').Append(F(w.HandoversPerSecond)).Append(',');
            sb.Append(w.GhostsHeld).Append(',').Append(w.AllocatedBytesPerTick).Append(',').Append('"').Append(w.Sections).Append('"');
            return sb.ToString();
        }

        private static string F(double v) => v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        private static string F0(double v) => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
