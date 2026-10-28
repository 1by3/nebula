using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Nebula.Tests
{
    /// <summary>
    /// A micro-benchmark of the far tier with crowds (D110, D111): 100 clients in a 4 km square, 500 far bodies among them
    /// in three classes (players, NPCs, creatures) and 128 hulls in the default class, for 10 simulated seconds at 60 Hz.
    /// The worker half is the real <see cref="FarPublisher"/> over a real <see cref="RegionPublisher"/> (two gateways, 50
    /// clients each); the gateway half is each client's evaluation at 4 Hz with the real <see cref="FarSelection"/> and
    /// every relay encoded with the real writers, at protocol 31 (compact poses) and at protocol 30 (full entries). It
    /// reports the worker's cost per tick, the gateway's per-client evaluation cost and the far bytes per client, and checks
    /// the budgets: no class over its own, and no hull ever evicted by a crowd.
    /// </summary>
    public sealed class FarTierBenchmarkTests
    {
        private const int Clients = 100, Hulls = 128, TickRate = 60, Seconds = 10;
        private const byte Pawn = 1, Npc = 2, Fauna = 3;

        private static readonly FarClass[] Classes =
        {
            new FarClass { Id = Pawn, Name = "pawn", MaxEntities = 32, UpdateRate = 2f, Radius = 1500f, Upright = true },
            new FarClass { Id = Npc, Name = "npc", MaxEntities = 64, UpdateRate = 1f, Radius = 1000f, Upright = true },
            new FarClass { Id = Fauna, Name = "fauna", MaxEntities = 48, UpdateRate = 0.5f, Radius = 800f, Upright = true },
        };

        private sealed class Body
        {
            public ulong NetId;
            public byte Class;
            public float Radius, Rate;
            public double X, Z;
            public Vector3 Velocity;
        }

        private sealed class Client
        {
            public double X, Z;
            public readonly Dictionary<ulong, (double X, double Y, double Z)> Held = new Dictionary<ulong, (double, double, double)>();
            public long Bytes31, Bytes30, Crowd31, Crowd30;
        }

        [Test]
        public void HundredClientsFiveHundredCrowdAndHundredTwentyEightHulls()
        {
            var settings = InterestSettings.Default;
            settings.FarMaxEntities = 128;
            settings.FarClasses = Classes;
            var grid = InterestGrid.Resolve(settings);
            var table = FarClassTable.From(settings);
            var random = new System.Random(1234);

            // Clients in five towns of a 4 km square; bodies around the same towns.
            var towns = new[] { (0.0, 0.0), (1500.0, 300.0), (-1200.0, 1400.0), (600.0, -1700.0), (-1800.0, -900.0) };
            var clients = new List<Client>();
            for (int i = 0; i < Clients; i++)
            {
                var (tx, tz) = towns[i % towns.Length];
                clients.Add(new Client { X = tx + random.NextDouble() * 400 - 200, Z = tz + random.NextDouble() * 400 - 200 });
            }
            var bodies = new List<Body>();
            void AddBodies(int count, byte cls)
            {
                for (int i = 0; i < count; i++)
                {
                    var (tx, tz) = towns[random.Next(towns.Length)];
                    table.Resolve(cls, 0f, 1f, out float radius, out float rate);
                    float heading = (float)(random.NextDouble() * Math.PI * 2);
                    bodies.Add(new Body
                    {
                        NetId = 0x0001_0000_0000_0000UL + (ulong)bodies.Count + 1, Class = cls, Radius = radius, Rate = rate,
                        X = tx + random.NextDouble() * 600 - 300, Z = tz + random.NextDouble() * 600 - 300,
                        Velocity = new Vector3(Mathf.Sin(heading), 0f, Mathf.Cos(heading)) * (cls == Fauna ? 3f : 1.5f),
                    });
                }
            }
            AddBodies(100, Pawn);
            AddBodies(300, Npc);
            AddBodies(100, Fauna);
            int crowd = bodies.Count;
            for (int i = 0; i < Hulls; i++)
            {
                double a = random.NextDouble() * Math.PI * 2, d = 5000 + random.NextDouble() * 45000;
                bodies.Add(new Body
                {
                    NetId = 0x0002_0000_0000_0000UL + (ulong)i + 1, Class = 0, Radius = 1e9f, Rate = 5f,
                    X = Math.Cos(a) * d, Z = Math.Sin(a) * d, Velocity = new Vector3(200f, 0f, 0f),
                });
            }

            // The worker's side: two gateways, 50 clients' foci each.
            var publisher = new RegionPublisher();
            ulong salt = RegionKeys.SaltOf(0UL);
            for (int g = 0; g < 2; g++)
            {
                var foci = new List<ulong>();
                for (int c = g; c < Clients; c += 2) foci.Add(grid.RegionOf(clients[c].X, 0, clients[c].Z) ^ salt);
                var receiver = new RegionSubscriptionReceiver();
                var msg = new InterestSubscribeMsg
                {
                    Seq = 1, Grid = grid, Flags = InterestSubscribeFlags.Full | InterestSubscribeFlags.Commit,
                    Add = new List<ulong>(), Remove = new List<ulong>(), FociRegions = foci, Entities = new List<ulong>(),
                    SetCount = 0, SetHash = RegionSubscription.Hash(new ulong[0]),
                };
                Assert.IsTrue(receiver.Apply(msg, out string rejection), rejection);
                publisher.AddGateway(g, receiver);
            }
            var far = new FarPublisher { EvalInterval = 1.0 / settings.EvalHz, ExitMargin = settings.ExitMargin };
            var workerWriter = new NetworkWriter(4096);
            long workerBytes = 0;
            var gatewayOf = new Func<int, ulong>(c => 1UL << (c % 2));

            // The gateway's side: the newest entry of each body, its holders, and a client's evaluation every 15 ticks, staggered.
            var records = new Dictionary<ulong, FarEntityEntry>();
            var holders = new Dictionary<ulong, List<int>>();
            var selection = new FarSelection();
            var chosen = new HashSet<ulong>();
            var scratch = new List<ulong>();
            var w = new NetworkWriter(256);
            int evalEvery = Mathf.RoundToInt(TickRate / settings.EvalHz);
            var workerTimer = new Stopwatch();
            var gatewayTimer = new Stopwatch();
            double workerWorst = 0;
            var classCounts = new int[256];
            int worstCrowdHeld = 0, hullsMissing = 0, overBudget = 0;

            int ticks = Seconds * TickRate;
            for (int tick = 1; tick <= ticks; tick++)
            {
                double now = tick / (double)TickRate;
                foreach (var b in bodies)
                {
                    b.X += b.Velocity.x / TickRate;
                    b.Z += b.Velocity.z / TickRate;
                }

                // ---- worker
                long started = Stopwatch.GetTimestamp();
                workerTimer.Start();
                far.Begin(now);
                foreach (var b in bodies)
                {
                    if (!far.IsDue(b.NetId)) { far.Keep(b.NetId); continue; }
                    far.Offer(new FarEntityEntry
                    {
                        NetId = b.NetId, Epoch = 1, Kind = FarEntryKind.State, PrefabId = (ushort)(10 + b.Class), FarClass = b.Class,
                        Radius = b.Radius, UpdateRate = b.Rate, X = b.X, Y = 0, Z = b.Z,
                        Rotation = Quaternion.LookRotation(b.Velocity.normalized), Velocity = b.Velocity,
                    }, salt, publisher, grid);
                }
                far.End();
                var sent = new List<FarPublisher.Outgoing>(far.Out);
                far.Flush(workerWriter, mask => workerBytes += workerWriter.Length);
                workerTimer.Stop();
                workerWorst = Math.Max(workerWorst, (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);

                // ---- gateway: relays
                gatewayTimer.Start();
                foreach (var o in sent)
                {
                    var e = o.Entry;
                    if (e.Kind != FarEntryKind.State) continue;
                    records[e.NetId] = e;
                    if (!holders.TryGetValue(e.NetId, out var list)) continue;
                    bool upright = table.Of(e.FarClass).Upright;
                    foreach (int c in list)
                    {
                        if ((o.Mask & gatewayOf(c)) == 0) continue;
                        var client = clients[c];
                        var anchor = client.Held[e.NetId];
                        w.Reset();
                        FarPosesMsg.WriteEntry(w, 1, upright, e.X - anchor.X, e.Y - anchor.Y, e.Z - anchor.Z, e.Rotation, e.Velocity);
                        client.Bytes31 += w.Length;
                        if (e.FarClass != 0) client.Crowd31 += w.Length;
                        w.Reset();
                        e.Write(w, 30);
                        client.Bytes30 += w.Length;
                        if (e.FarClass != 0) client.Crowd30 += w.Length;
                    }
                }

                // ---- gateway: evaluations
                for (int c = tick % evalEvery; c < Clients; c += evalEvery)
                {
                    var client = clients[c];
                    selection.Clear();
                    foreach (var e in records.Values)
                    {
                        double dx = e.X - client.X, dz = e.Z - client.Z, d2 = dx * dx + dz * dz;
                        if (d2 <= settings.Radius * settings.Radius) continue; // a replica, not a far entity
                        if (!FarRelevance.Within(d2, e.Radius, client.Held.ContainsKey(e.NetId), settings.ExitMargin)) continue;
                        selection.Add(table, e.NetId, e.FarClass, d2);
                    }
                    chosen.Clear();
                    selection.Choose(table, chosen);
                    scratch.Clear();
                    foreach (ulong id in client.Held.Keys) if (!chosen.Contains(id)) scratch.Add(id);
                    foreach (ulong id in scratch)
                    {
                        client.Held.Remove(id);
                        holders[id].Remove(c);
                        client.Bytes31 += 13;
                        client.Bytes30 += 13;
                    }
                    foreach (ulong id in chosen)
                    {
                        if (client.Held.ContainsKey(id)) continue;
                        var e = records[id];
                        client.Held[id] = (e.X, e.Y, e.Z);
                        if (!holders.TryGetValue(id, out var list)) holders[id] = list = new List<int>();
                        list.Add(c);
                        e.Slot = 1;
                        e.Age = 0.2f;
                        w.Reset();
                        e.Write(w, HelloMsg.FarClassesVersion);
                        client.Bytes31 += w.Length;
                        w.Reset();
                        e.Write(w, 30);
                        client.Bytes30 += w.Length;
                    }
                    if (tick > TickRate * 2)
                    {
                        Array.Clear(classCounts, 0, classCounts.Length);
                        foreach (ulong id in client.Held.Keys) classCounts[records[id].FarClass]++;
                        foreach (var cls in Classes) if (classCounts[cls.Id] > cls.MaxEntities) overBudget++;
                        worstCrowdHeld = Math.Max(worstCrowdHeld, classCounts[Pawn] + classCounts[Npc] + classCounts[Fauna]);
                        hullsMissing += Hulls - classCounts[0];
                    }
                }
                gatewayTimer.Stop();
            }

            double workerMs = workerTimer.Elapsed.TotalMilliseconds / ticks;
            double gatewayMs = gatewayTimer.Elapsed.TotalMilliseconds / ticks;
            long b31 = 0, b30 = 0, c31 = 0, c30 = 0;
            foreach (var c in clients) { b31 += c.Bytes31; b30 += c.Bytes30; c31 += c.Crowd31; c30 += c.Crowd30; }
            double perClient31 = b31 / (double)Clients / Seconds, perClient30 = b30 / (double)Clients / Seconds;
            string report =
                $"[far bench] {Clients} clients, {crowd} crowd + {Hulls} hulls, {Seconds} s at {TickRate} Hz\n" +
                $"  worker FarPublisher: {workerMs:0.000} ms/tick average, {workerWorst:0.000} ms worst, {workerBytes / (double)Seconds / 1024.0:0.0} KB/s to 2 gateways\n" +
                $"  gateway relay + evaluation (100 clients, 4 Hz): {gatewayMs:0.000} ms/tick average ({gatewayMs * TickRate / Clients:0.000} ms per client-second)\n" +
                $"  far bytes per client: {perClient31 / 1024.0:0.00} KB/s at protocol 31, {perClient30 / 1024.0:0.00} KB/s at protocol 30 ({perClient30 / Math.Max(1, perClient31):0.0}x)\n" +
                $"    of which crowd pose updates: {c31 / (double)Clients / Seconds / 1024.0:0.00} KB/s at 31, {c30 / (double)Clients / Seconds / 1024.0:0.00} KB/s at 30 (the rest: 128 hulls at 5 Hz, enters and leaves)\n" +
                $"  most crowd bodies one client held: {worstCrowdHeld} (budget {Classes[0].MaxEntities + Classes[1].MaxEntities + Classes[2].MaxEntities})";
            Debug.Log(report);
            TestContext.WriteLine(report);

            Assert.That(overBudget, Is.Zero, "no class over its own budget");
            Assert.That(hullsMissing, Is.Zero, "no hull evicted by the crowd");
            Assert.That(worstCrowdHeld, Is.GreaterThan(32), "the crowd classes are in use");
            Assert.That(perClient31, Is.LessThan(perClient30 * 0.6), "compact poses cut the far bytes");
            // Loose regression guards (a loaded CI machine is slow): the real numbers are in the log.
            Assert.That(workerMs, Is.LessThan(5.0), "the worker's far tier stays cheap");
            Assert.That(gatewayMs, Is.LessThan(20.0), "the gateway's far evaluation stays cheap");
        }
    }
}
