using System.Collections.Generic;
using System.Text;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// The pure half of per-container cost telemetry (docs/cost-telemetry.md): the wire round trip of the rows a
    /// worker adds to its telemetry document, how the reported entity cost sum feeds the planner's weights, and
    /// how a row's dominant component is decided. No Unity in here, so the standalone services run the same tests.
    /// </summary>
    public class ContainerCostTests
    {
        private const float TickMs = 1000f / 60f;
        private const double Link = NebulaConfig.DefaultCostLinkBytesPerSec;

        /// <summary>A document shaped exactly like the one <see cref="WorkerTelemetry"/> writes.</summary>
        private static string Document(string workerId, params string[] rows) =>
            "{\"worker\":\"" + workerId + "\",\"index\":1,\"tick\":10,\"detail\":false,\"containers\":[" + string.Join(",", rows) + "]}";

        private static string Row(string id, string scope, int players, int bots, int serverDriven, int ghosts, double cost, double tickMs, long bytesOut, long gatewayBytes) =>
            "{\"id\":\"" + id + "\",\"players\":" + players + ",\"bots\":" + bots + ",\"serverDriven\":" + serverDriven +
            ",\"other\":0,\"ghosts\":" + ghosts + ",\"scope\":\"" + scope + "\",\"cost\":" + cost.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"tickMs\":" + tickMs.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"bytesOut\":" + bytesOut + ",\"gatewayBytes\":" + gatewayBytes + "}";

        [Test]
        public void ParsesTheCostRowsAlongsideTheCountsItAlwaysParsed()
        {
            var loads = new List<KeyValuePair<string, ContainerLoad>>();
            var costs = new List<ContainerCost>();
            MeshTelemetry.ParseContainers(Document("w1",
                Row("arena", "", 3, 1, 0, 2, 14.5, 4.25, 90000, 12000),
                Row("lobby", "dungeon/7", 0, 0, 40, 0, 40.0, 1.5, 1000, 0)), loads, costs);

            Assert.AreEqual(2, loads.Count);
            Assert.AreEqual(3, loads[0].Value.Players);
            Assert.IsTrue(loads[0].Value.HasEntityCost);
            Assert.AreEqual(14.5f, loads[0].Value.EntityCostSum, 1e-3f);

            Assert.AreEqual(2, costs.Count);
            Assert.AreEqual("arena", costs[0].ContainerId);
            Assert.AreEqual("", costs[0].ScopeKey, "the public world's scope key is empty");
            Assert.AreEqual(4.25f, costs[0].TickShareMs, 1e-3f);
            Assert.AreEqual(90000, costs[0].BytesOutPerSec);
            Assert.AreEqual(12000, costs[0].GatewayBytesPerSec);
            Assert.AreEqual(2, costs[0].GhostCount);
            Assert.AreEqual("dungeon/7", costs[1].ScopeKey, "an instance's rows carry its scope key");
        }

        [Test]
        public void ADocumentFromAWorkerThatReportsNoCostStillBalancesTheMesh()
        {
            var loads = new List<KeyValuePair<string, ContainerLoad>>();
            var costs = new List<ContainerCost>();
            MeshTelemetry.ParseContainers(
                "{\"worker\":\"w1\",\"containers\":[{\"id\":\"arena\",\"players\":2,\"bots\":0,\"serverDriven\":0,\"other\":0,\"ghosts\":0}]}",
                loads, costs);

            Assert.AreEqual(1, loads.Count);
            Assert.IsFalse(loads[0].Value.HasEntityCost, "no cost key means the head count decides, as it always did");
            var weights = CostWeights.Default;
            Assert.AreEqual(weights.Base + 2f * weights.Player, weights.Of(loads[0].Value), 1e-4f);
            Assert.AreEqual(0f, costs[0].TickShareMs, 1e-6f);
        }

        [Test]
        public void TheReportedCostSumReplacesTheHeadCountAndMatchesItWhenNothingIsWeighted()
        {
            var weights = CostWeights.Default;
            // Two players and three bots, nobody weighted: the sum the worker tallies is what the heads come to.
            var plain = new ContainerLoad { Players = 2, Bots = 3, EntityCostSum = 2 * weights.Player + 3 * weights.Bot, HasEntityCost = true };
            Assert.AreEqual(weights.Base + 2 * weights.Player + 3 * weights.Bot, weights.Of(plain), 1e-4f);

            // The same heads with a boss among the bots: the mesh is told it costs more, without a new category.
            var weighted = plain;
            weighted.EntityCostSum += 7f * weights.Bot;
            Assert.Greater(weights.Of(weighted), weights.Of(plain));
        }

        [Test]
        public void TheDominantComponentIsWhicheverIsClosestToItsOwnBudget()
        {
            // Half a tick of simulation, almost no traffic.
            var sim = new ContainerCost { TickShareMs = TickMs * 0.5f, BytesOutPerSec = 1000, GatewayBytesPerSec = 0 };
            ContainerCost.Resolve(ref sim, TickMs, Link);
            Assert.AreEqual(CostComponent.Simulation, sim.Dominant);
            Assert.AreEqual(0.5f, sim.DominantSaturation, 1e-3f);

            // Barely any simulation, but most of the link: an interest problem, not a split problem.
            var rep = new ContainerCost { TickShareMs = TickMs * 0.05f, BytesOutPerSec = (long)(Link * 0.8), GatewayBytesPerSec = 10 };
            ContainerCost.Resolve(ref rep, TickMs, Link);
            Assert.AreEqual(CostComponent.Replication, rep.Dominant);
            Assert.AreEqual(0.8f, rep.DominantSaturation, 1e-3f);

            // A plaza of players: the relay is what hurts.
            var gw = new ContainerCost { TickShareMs = TickMs * 0.1f, BytesOutPerSec = (long)(Link * 0.2), GatewayBytesPerSec = (long)(Link * 0.6) };
            ContainerCost.Resolve(ref gw, TickMs, Link);
            Assert.AreEqual(CostComponent.Gateway, gw.Dominant);
        }

        [Test]
        public void TickShareIsTakenOverTheRowsOfOneWorkerOnly()
        {
            var rows = new List<ContainerCost>
            {
                new ContainerCost { ContainerId = "a", TickShareMs = 3f },
                new ContainerCost { ContainerId = "b", TickShareMs = 1f },
            };
            ContainerCost.Normalize(rows, TickMs, Link);
            Assert.AreEqual(0.75f, rows[0].TickShare, 1e-4f);
            Assert.AreEqual(0.25f, rows[1].TickShare, 1e-4f);

            // A worker that measured nothing yet must not divide by zero.
            var empty = new List<ContainerCost> { new ContainerCost { ContainerId = "a" } };
            ContainerCost.Normalize(empty, TickMs, Link);
            Assert.AreEqual(0f, empty[0].TickShare);
        }

        [Test]
        public void TheOrchestratorKeepsOneRowPerContainerAndReplacesAWorkersRowsWholesale()
        {
            double clock = 0.0;
            var telemetry = new MeshTelemetry(() => clock) { TickPeriodMs = TickMs, LinkBytesPerSec = Link };
            Assert.IsNull(telemetry.Accept(Document("w1", Row("arena", "", 4, 0, 0, 0, 16, 5, 200000, 40000)), out _));
            Assert.IsNull(telemetry.Accept(Document("w2", Row("lobby", "", 0, 0, 10, 0, 10, 1, 5000, 0)), out _));

            var rows = new Dictionary<string, ContainerCost>();
            telemetry.CopyContainerCost(rows);
            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual("w1", rows["arena"].WorkerId);
            Assert.AreEqual(1f, rows["arena"].TickShare, 1e-4f, "the only container of its worker takes all of its attributed time");

            // w1 hands the arena over and now holds nothing: its old row must not linger.
            clock = 1.0;
            Assert.IsNull(telemetry.Accept(Document("w1"), out _));
            telemetry.CopyContainerCost(rows);
            Assert.IsFalse(rows.ContainsKey("arena"));
            Assert.IsTrue(rows.ContainsKey("lobby"));

            // And a worker that goes away takes its rows with it.
            telemetry.Forget("w2");
            telemetry.CopyContainerCost(rows);
            Assert.AreEqual(0, rows.Count);
        }

        [Test]
        public void TheRowsSurviveAJsonRoundTripThroughTheApiDocument()
        {
            double clock = 0.0;
            var telemetry = new MeshTelemetry(() => clock) { TickPeriodMs = TickMs, LinkBytesPerSec = Link };
            telemetry.Accept(Document("w1",
                Row("arena", "raid/3", 6, 0, 0, 1, 24, 6.5, 250000, 48000),
                Row("lobby", "", 0, 0, 2, 0, 2, 0.5, 900, 0)), out _);

            string json = telemetry.BuildCostJson();
            StringAssert.Contains("\"id\":\"arena\"", json);
            StringAssert.Contains("\"scope\":\"raid/3\"", json);
            StringAssert.Contains("\"worker\":\"w1\"", json);
            StringAssert.Contains("\"tickMs\":6.5", json);
            StringAssert.Contains("\"bytesOut\":250000", json);
            StringAssert.Contains("\"gatewayBytes\":48000", json);
            StringAssert.Contains("\"dominant\":\"simulation\"", json);
            Assert.Less(json.IndexOf("arena", System.StringComparison.Ordinal), json.IndexOf("lobby", System.StringComparison.Ordinal), "heaviest first");

            // And the same writer the state document uses produces a row a reader can find its way through.
            var sb = new StringBuilder();
            var w = new JsonWriter(sb);
            var row = new ContainerCost { ContainerId = "arena", ScopeKey = "raid/3", WorkerId = "w1", EntityCostSum = 24f, TickShareMs = 6.5f, TickShare = 0.9f, BytesOutPerSec = 250000, GatewayBytesPerSec = 48000, GhostCount = 1 };
            ContainerCost.Resolve(ref row, TickMs, Link);
            ContainerCost.Write(w, row);
            StringAssert.Contains("\"cost\":24", sb.ToString());
            StringAssert.Contains("\"ghosts\":1", sb.ToString());
        }
    }
}
