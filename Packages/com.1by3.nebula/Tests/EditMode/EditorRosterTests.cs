using System.Collections.Generic;
using System.Text;
using NUnit.Framework;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-350: with several Multiplayer Play Mode virtual players enabled, exactly one hosts the server and the rest
    /// are clients, worked out from the roster in Library/VP/SystemData.json. Tested on the file's text.
    /// </summary>
    public sealed class EditorRosterTests
    {
        private const NebulaEditorRunMode Mppm = NebulaEditorRunMode.MultiplayerPlayMode;

        private const string Sample = @"{ ""Data"": {
            ""1"": { ""Name"": ""Main Editor"", ""Tags"": [], ""Active"": true, ""Index"": 1, ""Type"": 0,
                     ""PlayerIdentifier"": { ""Guid"": ""3ded"" }, ""TypeDependentPlayerInfo"": { ""VirtualProjectIdentifier"": null }, ""MultiplayerRole"": 3 },
            ""2"": { ""Name"": ""Player 2"", ""Tags"": [], ""Active"": false, ""Index"": 2, ""Type"": 1,
                     ""PlayerIdentifier"": { ""Guid"": ""67d7"" }, ""TypeDependentPlayerInfo"": { ""VirtualProjectIdentifier"": { ""m_Id"": ""98673e00"", ""m_Prefix"": ""mppm"" } }, ""MultiplayerRole"": 3 },
            ""4"": { ""Name"": ""Player 4"", ""Tags"": [], ""Active"": false, ""Index"": 4, ""Type"": 1,
                     ""TypeDependentPlayerInfo"": { ""VirtualProjectIdentifier"": null } } },
          ""IsMppmActive"": true, ""IsMutePlayers"": false }";

        private struct P
        {
            public int Index;
            public bool Active;
            public string[] Tags;
        }

        private static P Player(int index, bool active = true, params string[] tags) => new P { Index = index, Active = active, Tags = tags };

        /// <summary>A roster of a main Editor and the given virtual players, whose folder is "mppmp" plus the index.</summary>
        private static string Roster(params P[] players)
        {
            var sb = new StringBuilder("{\"Data\":{\"1\":{\"Name\":\"Main Editor\",\"Tags\":[],\"Active\":true,\"Index\":1,\"Type\":0,\"TypeDependentPlayerInfo\":{\"VirtualProjectIdentifier\":null}}");
            foreach (var p in players)
            {
                sb.Append(",\"").Append(p.Index).Append("\":{\"Name\":\"Player ").Append(p.Index).Append("\",\"Tags\":[");
                for (int i = 0; i < p.Tags.Length; i++) sb.Append(i > 0 ? "," : "").Append('"').Append(p.Tags[i]).Append('"');
                sb.Append("],\"Active\":").Append(p.Active ? "true" : "false").Append(",\"Index\":").Append(p.Index)
                  .Append(",\"Type\":1,\"TypeDependentPlayerInfo\":{\"VirtualProjectIdentifier\":{\"m_Id\":\"p").Append(p.Index).Append("\",\"m_Prefix\":\"mppm\"}}}");
            }
            return sb.Append("}}").ToString();
        }

        /// <summary>What the virtual player of this index does, given its own tags and the roster.</summary>
        private static EditorRunPlan Plan(string roster, int index, params string[] tags)
        {
            var facts = EditorRoster.Parse(roster, "mppmp" + index, out _);
            return EditorRunPlan.Resolve(Mppm, true, true, false, tags, false, facts);
        }

        [Test]
        public void TheLowestActiveVirtualPlayerHostsAndTheOthersAreClients()
        {
            string roster = Roster(Player(2), Player(3), Player(4));
            Assert.AreEqual(EditorPlayer.Server, Plan(roster, 2).Player);
            Assert.AreEqual(EditorPlayer.Client, Plan(roster, 3).Player);
            Assert.AreEqual(EditorPlayer.Client, Plan(roster, 4).Player);
            Assert.IsTrue(Plan(roster, 2).HidesGameView);
            Assert.IsFalse(Plan(roster, 3).HidesGameView, "a client keeps its Game view");
        }

        [Test]
        public void AnInactivePlayerDoesNotCount()
        {
            string roster = Roster(Player(2, active: false), Player(3), Player(4));
            Assert.AreEqual(EditorPlayer.Server, Plan(roster, 3).Player);
            Assert.AreEqual(EditorPlayer.Client, Plan(roster, 4).Player);
        }

        [Test]
        public void AClientTaggedPlayerNeverHostsSoTheNextOneDoes()
        {
            string roster = Roster(Player(2, true, "Client"), Player(3), Player(4));
            Assert.AreEqual(EditorPlayer.Client, Plan(roster, 2, "Client").Player);
            Assert.AreEqual(EditorPlayer.Server, Plan(roster, 3).Player);
            Assert.AreEqual(EditorPlayer.Client, Plan(roster, 4).Player);
        }

        [Test]
        public void AServerTaggedPlayerMakesEveryUntaggedOneAClient()
        {
            string roster = Roster(Player(2), Player(3), Player(4, true, "Server"));
            Assert.AreEqual(EditorPlayer.Client, Plan(roster, 2).Player);
            Assert.AreEqual(EditorPlayer.Client, Plan(roster, 3).Player);
            Assert.AreEqual(EditorPlayer.Server, Plan(roster, 4, "Server").Player);
            Assert.IsNull(Plan(roster, 4, "Server").Error);
        }

        [Test]
        public void OnlyTheLowestOfSeveralServerTaggedPlayersHostsAndTheOthersLogAnError()
        {
            string roster = Roster(Player(2, true, "Server"), Player(3, true, "server"), Player(4));
            var first = Plan(roster, 2, "Server");
            var second = Plan(roster, 3, "server");
            Assert.AreEqual(EditorPlayer.Server, first.Player);
            Assert.IsNull(first.Error);
            Assert.AreEqual(EditorPlayer.Client, second.Player, "a second server would bind the same ports and save file");
            StringAssert.Contains("more than one", second.Error);
            Assert.IsFalse(second.HidesGameView);
            Assert.AreEqual(EditorPlayer.Client, Plan(roster, 4).Player);
        }

        [Test]
        public void ADuplicateServerTagOnAnInactivePlayerIsIgnored()
        {
            string roster = Roster(Player(2, false, "Server"), Player(3, true, "Server"));
            Assert.AreEqual(EditorPlayer.Server, Plan(roster, 3, "Server").Player);
            Assert.IsNull(Plan(roster, 3, "Server").Error);
        }

        [Test]
        public void AnUnreadableRosterOrAnUnlistedPlayerKeepsTheOlderRule()
        {
            foreach (string json in new[] { null, "", "not json", "{\"Data\":", "{\"Other\":{}}", Roster(Player(2), Player(3)) })
            {
                var facts = EditorRoster.Parse(json, "mppmp9", out string problem);
                Assert.IsFalse(facts.Known);
                Assert.IsNotNull(problem);
                var plan = EditorRunPlan.Resolve(Mppm, true, true, false, new string[0], false, facts);
                Assert.AreEqual(EditorPlayer.Server, plan.Player, "an untagged virtual player hosts, as before");
                Assert.IsTrue(plan.HidesGameView);
            }
            Assert.IsFalse(EditorRosterReader.Read(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nebula-no-such-project"), "mppmp2", out string missing).Known);
            StringAssert.Contains("does not exist", missing);
        }

        [Test]
        public void TheMainEditorIsAlwaysAClient()
        {
            var facts = EditorRoster.Parse(Roster(Player(2)), "mppmp2", out _);
            Assert.AreEqual(EditorPlayer.Client, EditorRunPlan.Resolve(Mppm, true, true, true, new string[0], false, facts).Player);
        }

        [Test]
        public void TheRealFileTheEditorWritesParses()
        {
            // Player 2 is inactive in the sample, but a clone that runs is treated as active; Player 4 has no folder.
            var facts = EditorRoster.Parse(Sample, "mppm98673e00", out string problem);
            Assert.IsNull(problem);
            Assert.IsTrue(facts.Known);
            Assert.IsFalse(facts.AnyServerTagged);
            Assert.IsFalse(facts.LowerIndexServerTagged);
            Assert.IsTrue(facts.IsFirstHostCandidate);
            Assert.AreEqual("Player 2", facts.PlayerName, "a client clone's default name shows its name in the Multiplayer Play Mode window");
            Assert.IsFalse(EditorRoster.Parse(Sample, "mppm00000000", out _).Known);
        }

        [Test]
        public void TheParserReadsEscapesNumbersAndNesting()
        {
            string bom = ((char)0xFEFF).ToString();
            var v = MiniJson.Parse(bom + " {\"a\": [1, -2.5e1, true, false, null, \"x\\n\\\"\"], \"b\": {}} ") as Dictionary<string, object>;
            Assert.IsNotNull(v);
            var a = (List<object>)v["a"];
            Assert.AreEqual(1d, a[0]);
            Assert.AreEqual(-25d, a[1]);
            Assert.AreEqual(true, a[2]);
            Assert.IsNull(a[4]);
            Assert.AreEqual("x\n\"", a[5]);
            Assert.IsNull(MiniJson.Parse("{\"a\": 1} trailing"));
            Assert.IsNull(MiniJson.Parse("[1, 2"));
        }

        [TestCase("C:/Dev/game/Library/VP/mppm531add0a/Assets", "mppm531add0a")]
        [TestCase(@"C:\Dev\game\Library\VP\mppm1\Assets\", "mppm1")]
        [TestCase("C:/Dev/game/Assets", null)]
        [TestCase("", null)]
        public void TheVirtualPlayerFolderComesFromTheDataPath(string dataPath, string folder)
        {
            Assert.AreEqual(folder, EditorDevPaths.VirtualPlayerFolder(dataPath));
        }

        [Test]
        public void EachVirtualPlayerClientKeepsItsOwnIdentity()
        {
            Assert.AreEqual("nebula.identityToken", NebulaClient.IdentityKey(null), "the main Editor keeps its existing identity");
            Assert.AreEqual("nebula.identityToken", NebulaClient.IdentityKey(""));
            Assert.AreEqual("nebula.identityToken.mppm98673e00", NebulaClient.IdentityKey("mppm98673e00"));
            Assert.AreNotEqual(NebulaClient.IdentityKey("mppm1"), NebulaClient.IdentityKey("mppm2"));
        }
    }
}
