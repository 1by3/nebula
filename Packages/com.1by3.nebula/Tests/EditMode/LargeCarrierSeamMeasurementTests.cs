using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// What it costs two workers when a large loaded carrier crosses the seam between them (NEB-498). A 100 m carrier
    /// with its own physics frame (box 40 x 16 x 100 m: the deck and a 12 m envelope over it) carries 0 to 500 attached
    /// build pieces (<see cref="FrameAttachment"/>), two ships with frames of their own (10 x 6 x 20 m) with two riders
    /// each, and four riders standing on its deck. It flies back and forth across the seam at 300 m/s and at 50 m/s,
    /// with and without an explicit extent (<c>docs/entity-extents.md</c>), and every crossing is measured: what the
    /// sending worker's tick cost, what the receiving worker spent applying what it was sent and on its first tick
    /// with the group, the bytes and messages of the handover, how many ticks it spanned, and where the receiver's time
    /// went (per message kind and per entity kind).
    /// <para>
    /// A measurement, not a test of a rule: <c>[Explicit]</c>, so an ordinary run never picks it up. Run it with
    /// <c>-testFilter Nebula.Tests.LargeCarrierSeamMeasurementTests</c>; it writes
    /// <c>Logs/scale/editor-large-carrier-seam.csv</c> and prints a table. Real <see cref="NebulaWorker"/>s on the
    /// in-process <see cref="ConformanceMesh"/>: times are the Editor's (Mono, no player optimisations, physics frames in
    /// preview scenes, <c>Debug.Log</c> writing to the Editor log), so they are a proxy for a built worker, not its numbers.
    /// Compare rows, not machines. Every crossing is a freshly built carrier's first, 200 m from the seam: the receiver
    /// holds only what the ghost band gave it on the way (with the extent, the whole load some ticks ahead; without it,
    /// nothing or the tick before), never a copy left by an earlier crossing. Five per configuration, the first a warm-up.
    /// </para>
    /// </summary>
    [Explicit("measurement: run on purpose, writes Logs/scale/editor-large-carrier-seam.csv")]
    [Category("Scale")]
    public sealed class LargeCarrierSeamMeasurementTests
    {
        private static float Dt => NetworkTime.TickInterval;
        private const int Crossings = 5; // crossings per configuration, each by a freshly built carrier; the first is a warm-up
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Vector3 CarrierSize = new Vector3(40f, 16f, 100f);
        private static readonly Vector3 CarrierCenter = new Vector3(0f, 6f, 0f);

        private static readonly List<string> Rows = new List<string>();
        private static readonly List<string> Table = new List<string>();
        private static readonly List<string> Breakdown = new List<string>();
        private const string Header = "pieces,speed,extent,logging,entities,crossings,send_med_ms,send_max_ms,apply_med_ms,apply_max_ms,recv_tick_med_ms,recv_tick_max_ms,recv_frame_med_ms,recv_frame_max_ms,ghost_spawn_med_ms,ghost_spawn_max_ms,ghosted_before,transfers,handover_bytes,carrier_bytes,ship_bytes,rider_bytes,piece_bytes,ghost_spawn_bytes,span_ticks,lead_ticks,cruise_tick_med_ms,partition_warnings";

        private ConformanceMesh _mesh;
        private Container _west, _east;

        [SetUp]
        public void SetUp()
        {
            PhysicsFrames.SceneFactory = () => EditorSceneManager.NewPreviewScene();
            PhysicsFrames.SceneDisposer = s => EditorSceneManager.ClosePreviewScene(s);
        }

        [TearDown]
        public void TearDown()
        {
            Debug.unityLogger.logEnabled = true;
            ContainerRegistry.WorkerIdByIndex = index => NebulaRuntime.IsServer && index == NebulaRuntime.LocalWorkerIndex ? NebulaRuntime.LocalWorkerId : "";
            _mesh?.Dispose();
            _mesh = null;
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            PhysicsFrames.SceneFactory = null;
            PhysicsFrames.SceneDisposer = null;
        }

        [OneTimeTearDown]
        public void Write()
        {
            if (Rows.Count == 0) return;
            var lines = new List<string> { Header };
            lines.AddRange(Rows);
            string path = CrowdMesh.ArtifactPath("editor-large-carrier-seam.csv");
            File.WriteAllLines(path, lines);
            var sb = new StringBuilder();
            sb.AppendLine("[scale:editor] large carrier across a seam, wrote " + path);
            sb.AppendLine("| pieces | speed | extent | log | entities | send ms med/max | apply ms med/max | recv tick ms med/max | recv frame ms med/max | ghost spawn ms med/max | ghosted before | transfers | handover bytes (carrier / ship / rider / piece) | ghost spawn bytes | span | lead ticks | cruise tick ms | partition warnings |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var t in Table) sb.AppendLine(t);
            sb.AppendLine();
            sb.AppendLine("receiver's apply time by message and entity kind (ms, summed over a crossing, median of the measured crossings):");
            foreach (var b in Breakdown) sb.AppendLine(b);
            Debug.Log(sb.ToString());
            TestContext.WriteLine(sb.ToString());
        }

        // ------------------------------------------------------------------------------------ fixtures

        private static void Author(NetworkIdentity identity, Bounds box)
        {
            typeof(NetworkIdentity).GetField("_extentSource", Private).SetValue(identity, EntityExtentSource.Explicit);
            typeof(NetworkIdentity).GetField("_extent", Private).SetValue(identity, box);
        }

        private static void Deck(GameObject owner, Vector3 size)
        {
            var deck = new GameObject("deck");
            deck.transform.SetParent(owner.transform, false);
            deck.transform.localPosition = new Vector3(0f, -0.1f, 0f);
            deck.AddComponent<BoxCollider>().size = size;
        }

        private ushort CarrierPrefab(bool extent)
        {
            var carrier = new GameObject("carrier-prefab");
            var identity = carrier.AddComponent<NetworkIdentity>();
            var hull = carrier.AddComponent<Container>();
            hull.ContainerId = "carrier";
            hull.Size = CarrierSize;
            hull.Center = CarrierCenter;
            hull.OwnPhysicsFrame = true;
            carrier.AddComponent<NetworkTransform>();
            Deck(carrier, new Vector3(CarrierSize.x, 0.2f, CarrierSize.z));
            if (extent) Author(identity, new Bounds(CarrierCenter, CarrierSize));
            return _mesh.RegisterPrefab(carrier);
        }

        private ushort ShipPrefab()
        {
            var ship = new GameObject("ship-prefab");
            ship.AddComponent<NetworkIdentity>();
            var box = ship.AddComponent<Container>();
            box.ContainerId = "ship";
            box.Size = new Vector3(10f, 6f, 20f);
            box.Center = new Vector3(0f, 3f, 0f);
            box.OwnPhysicsFrame = true;
            ship.AddComponent<NetworkTransform>();
            Deck(ship, new Vector3(10f, 0.2f, 20f));
            return _mesh.RegisterPrefab(ship);
        }

        private ushort RiderPrefab()
        {
            var rider = new GameObject("rider-prefab");
            rider.AddComponent<NetworkIdentity>();
            rider.AddComponent<NetworkTransform>();
            var capsule = rider.AddComponent<CapsuleCollider>();
            capsule.height = 1.8f;
            capsule.radius = 0.4f;
            return _mesh.RegisterPrefab(rider);
        }

        /// <summary>A build piece: a 2 m panel with a collider, attached to the carrier's box.</summary>
        private ushort PiecePrefab()
        {
            var piece = new GameObject("piece-prefab");
            piece.AddComponent<NetworkIdentity>();
            piece.AddComponent<BoxCollider>().size = new Vector3(2f, 2f, 0.2f);
            piece.AddComponent<FrameAttachment>();
            return _mesh.RegisterPrefab(piece);
        }

        private sealed class Loaded
        {
            public NetworkIdentity Carrier;
            public readonly Dictionary<ulong, string> Kind = new Dictionary<ulong, string>();
            public int Count => Kind.Count;
        }

        private Loaded Build(int pieces, bool extent)
        {
            _mesh = new ConformanceMesh(2, perWorkerRegistry: true);
            ContainerRegistry.WorkerIdByIndex = index => index == 1 ? "w1" : index == 2 ? "w2" : "";
            var size = new Vector3(2000f, 400f, 2000f);
            _west = _mesh.AddStaticContainer("west", new Vector3(-1000f, 0f, 0f), size);
            _east = _mesh.AddStaticContainer("east", new Vector3(1000f, 0f, 0f), size);
            _west.Center = Vector3.zero;
            _east.Center = Vector3.zero;
            ContainerRegistry.Rebuild();
            _mesh.SetOwner(_west, _mesh[0]);
            _mesh.SetOwner(_east, _mesh[1]);
            ushort carrierPrefab = CarrierPrefab(extent), shipPrefab = ShipPrefab(), riderPrefab = RiderPrefab(), piecePrefab = PiecePrefab();

            var w1 = _mesh[0];
            var loaded = new Loaded();
            // Heading +x, across the seam at x = 0.
            loaded.Carrier = w1.SpawnServerDriven(carrierPrefab, _west, new Vector3(-200f, 0f, 0f), Quaternion.Euler(0f, 90f, 0f));
            var box = loaded.Carrier.Carried;
            loaded.Kind[loaded.Carrier.NetId] = "carrier";
            foreach (float x in new[] { -10f, 10f })
            {
                var ship = ConformanceFrameBodiesTests.SpawnIn(w1, shipPrefab, box, new Vector3(x, 0f, -25f), Quaternion.identity);
                loaded.Kind[ship.NetId] = "ship";
                foreach (float z in new[] { 3f, -3f })
                    loaded.Kind[ConformanceFrameBodiesTests.SpawnIn(w1, riderPrefab, ship.Carried, new Vector3(0f, 0.9f, z), Quaternion.identity).NetId] = "rider";
            }
            foreach (var at in new[] { new Vector3(-12f, 0.9f, 30f), new Vector3(12f, 0.9f, 30f), new Vector3(-12f, 0.9f, 40f), new Vector3(12f, 0.9f, 40f) })
                loaded.Kind[ConformanceFrameBodiesTests.SpawnIn(w1, riderPrefab, box, at, Quaternion.identity).NetId] = "rider";
            for (int i = 0; i < pieces; i++)
            {
                // Rows of panels over the deck, stacked three high where the deck runs out.
                int column = i % 18, row = (i / 18) % 30, layer = i / (18 * 30);
                var local = new Vector3(-17f + column * 2f, 1f + layer * 2.2f, 2f + row * 1.6f);
                var piece = ConformanceFrameBodiesTests.SpawnIn(w1, piecePrefab, box, local, Quaternion.identity);
                Assume.That(piece.GetComponent<FrameAttachment>().Attach(), Is.True);
                loaded.Kind[piece.NetId] = "piece";
            }
            return loaded;
        }

        // ------------------------------------------------------------------------------------ the record

        private struct Delivery
        {
            public uint Tick;
            public int To;
            public MsgId Id;
            public string Kind;
            public ulong NetId;
            public int Bytes;
            public double Ms;
        }

        private struct TickCost
        {
            public double W1, W2;
            /// <summary>The worker that did not simulate the carrier held a copy of it (a ghost) before this tick's messages were applied.</summary>
            public bool OtherHeldCarrier;
        }

        private sealed class Crossing
        {
            public uint Tick;
            public int Receiver;
            public double SendMs, ApplyMs, ReceiverTickMs, GhostSpawnMs;
            public bool GhostedBefore;
            public int Transfers, Bytes, GhostSpawnBytes, Span, Lead;
            public readonly Dictionary<string, int> BytesByKind = new Dictionary<string, int>();
            public readonly Dictionary<string, int> CountByKind = new Dictionary<string, int>();
            public readonly Dictionary<string, double> ApplyByMessage = new Dictionary<string, double>();
            public double ReceiverFrameMs => ApplyMs + ReceiverTickMs;
        }

        private static double Median(IEnumerable<double> values)
        {
            var list = values.OrderBy(v => v).ToList();
            return list.Count == 0 ? 0 : list[list.Count / 2];
        }

        private static string Kind(Loaded loaded, ConformanceMesh.WireMessage message, out ulong netId)
        {
            netId = 0;
            try
            {
                if (message.Id == MsgId.AuthorityTransfer) netId = message.Read(AuthorityTransferMsg.Read).Entity.NetId;
                else if (message.Id == MsgId.GhostSpawn) netId = message.Read(EntitySpawnMsg.Read).NetId;
                else return "";
            }
            catch (Exception) { return "?"; }
            return loaded.Kind.TryGetValue(netId, out var kind) ? kind : "other";
        }

        /// <summary>
        /// One crossing of the seam by a freshly built, freshly loaded carrier, west to east: it starts 200 m west of the
        /// seam, so whatever the receiver holds of it when it crosses is what the ghost band gave it on the way, and
        /// nothing a previous crossing left behind.
        /// </summary>
        private Crossing CrossOnce(int pieces, float speed, bool extent, bool logging, List<double> cruise, ref int partitionWarnings, Dictionary<MsgId, (int Count, int Bytes, double Ms)> traffic, out int entities)
        {
            var loaded = Build(pieces, extent);
            entities = loaded.Count;
            Debug.unityLogger.logEnabled = logging;
            int warnings = 0;
            void Count(string condition, string stack, LogType type)
            {
                if (condition != null && condition.IndexOf("partition", StringComparison.OrdinalIgnoreCase) >= 0) warnings++;
            }
            Application.logMessageReceived += Count;
            var deliveries = new List<Delivery>();
            var costs = new List<TickCost> { default };
            uint tick = 1;
            _mesh.Timed = (message, ms) =>
            {
                string kind = Kind(loaded, message, out ulong netId);
                deliveries.Add(new Delivery { Tick = tick, To = message.To == "w1" ? 0 : 1, Id = message.Id, Kind = kind, NetId = netId, Bytes = message.Bytes.Length, Ms = ms });
            };
            ulong carrierId = loaded.Carrier.NetId;
            try
            {
                loaded.Carrier.transform.position = new Vector3(-200f, 0f, 0f);
                uint handedAt = 0;
                for (int guard = 0; guard < 100000; guard++, tick++)
                {
                    NetworkIdentity carrier = null;
                    int owner = -1;
                    for (int w = 0; w < 2; w++)
                    {
                        var e = _mesh[w].Find(carrierId);
                        if (e != null && e.HasAuthority) { carrier = e; owner = w; }
                    }
                    Assert.IsNotNull(carrier, $"t{tick}: somebody simulates the carrier");
                    if (owner == 1 && handedAt == 0) handedAt = tick - 1;
                    if (handedAt != 0 && tick > handedAt + 3) break;
                    carrier.Motion.Velocity = new Vector3(speed, 0f, 0f);
                    carrier.transform.position += carrier.Motion.Velocity * Dt;
                    var cost = new TickCost();
                    var sw = Stopwatch.StartNew();
                    _mesh[0].Tick(tick);
                    cost.W1 = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    _mesh[1].Tick(tick);
                    cost.W2 = sw.Elapsed.TotalMilliseconds;
                    cost.OtherHeldCarrier = _mesh[1 - owner].Find(carrierId) != null;
                    costs.Add(cost);
                    _mesh.Pump();
                }
                Assert.That(handedAt, Is.GreaterThan(0u), "the carrier crossed the seam");
            }
            finally
            {
                Application.logMessageReceived -= Count;
                _mesh.Timed = null;
                Debug.unityLogger.logEnabled = true;
            }
            partitionWarnings += warnings;
            foreach (var d in deliveries)
            {
                traffic.TryGetValue(d.Id, out var t);
                traffic[d.Id] = (t.Count + 1, t.Bytes + d.Bytes, t.Ms + d.Ms);
            }

            var h = deliveries.First(d => d.Id == MsgId.AuthorityTransfer && d.NetId == carrierId);
            var c = new Crossing { Tick = h.Tick, Receiver = h.To };
            c.SendMs = costs[(int)h.Tick].W1;
            c.ReceiverTickMs = h.Tick + 1 < costs.Count ? costs[(int)h.Tick + 1].W2 : 0;
            var group = deliveries.Where(d => d.To == 1 && d.Id == MsgId.AuthorityTransfer && loaded.Kind.ContainsKey(d.NetId)).ToList();
            c.Transfers = group.Count(d => d.Tick == h.Tick);
            c.Span = group.Select(d => d.Tick).Distinct().Count();
            foreach (var d in deliveries.Where(d => d.To == 1 && d.Tick == h.Tick))
            {
                c.ApplyMs += d.Ms;
                string key = d.Id + (d.Kind != "" ? ":" + d.Kind : "");
                c.ApplyByMessage[key] = (c.ApplyByMessage.TryGetValue(key, out var m) ? m : 0) + d.Ms;
            }
            foreach (var d in group.Where(d => d.Tick == h.Tick))
            {
                c.Bytes += d.Bytes;
                c.BytesByKind[d.Kind] = (c.BytesByKind.TryGetValue(d.Kind, out var b) ? b : 0) + d.Bytes;
                c.CountByKind[d.Kind] = (c.CountByKind.TryGetValue(d.Kind, out var n) ? n : 0) + 1;
            }
            // What the band gave the receiver before the handover: the ghost spawns, and the receiver's frame on the tick
            // they came (applying them, then its next tick).
            c.GhostedBefore = costs[(int)h.Tick].OtherHeldCarrier;
            var spawns = deliveries.Where(d => d.To == 1 && d.Id == MsgId.GhostSpawn && loaded.Kind.ContainsKey(d.NetId) && d.Tick < h.Tick).ToList();
            c.GhostSpawnBytes = spawns.Sum(d => d.Bytes);
            foreach (var g in spawns.GroupBy(d => d.Tick))
            {
                double frame = g.Sum(d => d.Ms) + (g.Key + 1 < costs.Count ? costs[(int)g.Key + 1].W2 : 0);
                c.GhostSpawnMs = Math.Max(c.GhostSpawnMs, frame);
            }
            var carrierSpawn = spawns.Where(d => d.NetId == carrierId).ToList();
            c.Lead = carrierSpawn.Count > 0 ? (int)(h.Tick - carrierSpawn.Min(d => d.Tick)) : 0;
            // Cruise: the ticks before anything of the crossing started, on the carrier's own worker.
            uint quiet = carrierSpawn.Count > 0 ? carrierSpawn.Min(d => d.Tick) : h.Tick;
            for (int t = 5; t + 2 < quiet && t < costs.Count; t++) cruise.Add(costs[t].W1);

            _mesh.Dispose();
            _mesh = null;
            ContainerRegistry.Rebuild();
            PhysicsFrames.DrainPool();
            return c;
        }

        private void Fly(int pieces, float speed, bool extent, bool logging)
        {
            var measured = new List<Crossing>();
            var cruise = new List<double>();
            var traffic = new Dictionary<MsgId, (int Count, int Bytes, double Ms)>();
            int partitionWarnings = 0, entities = 0;
            for (int run = 0; run < Crossings; run++)
            {
                var c = CrossOnce(pieces, speed, extent, logging, run == 0 ? new List<double>() : cruise, ref partitionWarnings, run == 0 ? new Dictionary<MsgId, (int, int, double)>() : traffic, out entities);
                if (run > 0) measured.Add(c); // the first is a warm-up
            }
            Report(pieces, speed, extent, logging, entities, measured, cruise, traffic, partitionWarnings);
        }

        private void Report(int pieces, float speed, bool extent, bool logging, int entities, List<Crossing> measured, List<double> cruise, Dictionary<MsgId, (int Count, int Bytes, double Ms)> traffic, int partitionWarnings)
        {
            int Avg(string kind) => (int)Math.Round(Median(measured.Select(c => c.BytesByKind.TryGetValue(kind, out var b) && c.CountByKind.TryGetValue(kind, out var n) && n > 0 ? (double)b / n : 0)));
            double MedianOf(Func<Crossing, double> f) => Median(measured.Select(f));
            double MaxOf(Func<Crossing, double> f) => measured.Max(f);
            string row = string.Join(",", pieces, speed, extent, logging, entities, measured.Count,
                MedianOf(c => c.SendMs).ToString("0.00"), MaxOf(c => c.SendMs).ToString("0.00"),
                MedianOf(c => c.ApplyMs).ToString("0.00"), MaxOf(c => c.ApplyMs).ToString("0.00"),
                MedianOf(c => c.ReceiverTickMs).ToString("0.00"), MaxOf(c => c.ReceiverTickMs).ToString("0.00"),
                MedianOf(c => c.ReceiverFrameMs).ToString("0.00"), MaxOf(c => c.ReceiverFrameMs).ToString("0.00"),
                MedianOf(c => c.GhostSpawnMs).ToString("0.00"), MaxOf(c => c.GhostSpawnMs).ToString("0.00"),
                measured.Count(c => c.GhostedBefore), (int)MedianOf(c => c.Transfers), (int)MedianOf(c => c.Bytes),
                Avg("carrier"), Avg("ship"), Avg("rider"), Avg("piece"), (int)MedianOf(c => c.GhostSpawnBytes),
                (int)MedianOf(c => c.Span), (int)MedianOf(c => c.Lead), Median(cruise).ToString("0.000"), partitionWarnings);
            Rows.Add(row);
            Table.Add($"| {pieces} | {speed:0} | {(extent ? "yes" : "no")} | {(logging ? "on" : "off")} | {entities} | {MedianOf(c => c.SendMs):0.0} / {MaxOf(c => c.SendMs):0.0} | {MedianOf(c => c.ApplyMs):0.0} / {MaxOf(c => c.ApplyMs):0.0} | {MedianOf(c => c.ReceiverTickMs):0.0} / {MaxOf(c => c.ReceiverTickMs):0.0} | {MedianOf(c => c.ReceiverFrameMs):0.0} / {MaxOf(c => c.ReceiverFrameMs):0.0} | {MedianOf(c => c.GhostSpawnMs):0.0} / {MaxOf(c => c.GhostSpawnMs):0.0} | {measured.Count(c => c.GhostedBefore)}/{measured.Count} | {(int)MedianOf(c => c.Transfers)} | {(int)MedianOf(c => c.Bytes)} ({Avg("carrier")} / {Avg("ship")} / {Avg("rider")} / {Avg("piece")}) | {(int)MedianOf(c => c.GhostSpawnBytes)} | {(int)MedianOf(c => c.Span)} | {(int)MedianOf(c => c.Lead)} | {Median(cruise):0.000} | {partitionWarnings} |");
            var keys = measured.SelectMany(c => c.ApplyByMessage.Keys).Distinct().OrderBy(k => k);
            Breakdown.Add($"  {pieces} pieces, {speed:0} m/s, extent {(extent ? "yes" : "no")}, logging {(logging ? "on" : "off")}: handover tick " +
                string.Join(", ", keys.Select(k => $"{k} {Median(measured.Select(c => c.ApplyByMessage.TryGetValue(k, out var v) ? v : 0)):0.00}")) +
                "; all deliveries of the measured crossings: " + string.Join(", ", traffic.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} x{kv.Value.Count} ({kv.Value.Bytes} B, {kv.Value.Ms:0.0} ms)")));
            Debug.Log("[scale:editor] " + row);
        }

        // ------------------------------------------------------------------------------------ the measurements

        /// <summary>
        /// One configuration. <paramref name="logging"/> off turns the Editor's log off for the flight: a worker logs a
        /// line for every entity it hands over and every one it receives, and this shows how much of the cost that is.
        /// </summary>
        [Test]
        public void ALoadedCarrierCrossesASeam([Values(0, 100, 300, 500)] int pieces, [Values(300f, 50f)] float speed,
            [Values(false, true)] bool extent, [Values(true, false)] bool logging)
        {
            Fly(pieces, speed, extent, logging);
        }

        /// <summary>
        /// The pieces of the receive path on their own, on one worker in the same process: instantiating each prefab, and
        /// spawning it (which for a framed carrier builds its frame scene and copies its colliders, and for a piece
        /// attaches it). Medians over 50.
        /// </summary>
        [Test]
        public void TheReceivePathInPieces()
        {
            var loaded = Build(0, extent: true);
            var w2 = _mesh[1];
            var sb = new StringBuilder("[scale:editor] receive path, per entity, median of 50 (ms):\n");
            foreach (var (name, prefab) in new[] { ("carrier", (ushort)0), ("ship", (ushort)1), ("rider", (ushort)2), ("piece", (ushort)3) })
            {
                var instantiate = new List<double>();
                var spawn = new List<double>();
                for (int i = 0; i < 50; i++)
                {
                    NetworkIdentity e = null;
                    var sw = Stopwatch.StartNew();
                    w2.Act(() => e = NetworkPrefabs.Instantiate(prefab, new Vector3(1000f + 200f * i, 0f, 500f), Quaternion.identity, _east.transform));
                    instantiate.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart();
                    w2.Act(() => w2.Instance.SpawnServerDriven(e, _east));
                    spawn.Add(sw.Elapsed.TotalMilliseconds);
                    if (name == "piece")
                    {
                        sw.Restart();
                        e.GetComponent<FrameAttachment>().Attach();
                        spawn[spawn.Count - 1] += sw.Elapsed.TotalMilliseconds;
                    }
                }
                sb.AppendLine($"  {name}: instantiate {Median(instantiate):0.000}, spawn (frame scene, collider copies, attach) {Median(spawn):0.000}");
            }
            Breakdown.Add(sb.ToString().TrimEnd());
            Debug.Log(sb.ToString());
            Assert.IsNotNull(loaded.Carrier);
        }
    }
}
