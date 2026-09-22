using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Nebula.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Nebula.Tests
{
    /// <summary>
    /// The developer-time diagnostics behind the distributed physics model: <c>Nebula &gt; Validate Project</c>
    /// and the worker warn about a joint whose bodies live in different containers, and an <c>AuthorityRpc</c> from
    /// a copy that is neither authoritative nor a ghost is discarded and counted.
    /// </summary>
    [Category("Conformance")]
    public sealed class ConformancePhysicsDiagnosticTests
    {
        private const string Tag = "physdiag";
        private readonly List<GameObject> _created = new List<GameObject>();
        private bool _wasServer, _wasClient;
        private IRpcSink _previousSink;

        [SetUp]
        public void SetUp()
        {
            _wasServer = NebulaRuntime.IsServer;
            _wasClient = NebulaRuntime.IsClient;
            _previousSink = NebulaRuntime.RpcSink;
            PhysicsIslands.ResetForNewSession();
            PhysicsIslands.IsCohesive = null;
        }

        [TearDown]
        public void TearDown()
        {
            PhysicsIslands.IsCohesive = null;
            NebulaRuntime.IsServer = _wasServer;
            NebulaRuntime.IsClient = _wasClient;
            NebulaRuntime.RpcSink = _previousSink;
            foreach (var go in _created) if (go != null) Object.DestroyImmediate(go);
            _created.Clear();
        }

        private GameObject Make(string name, Transform parent = null, Vector3 position = default)
        {
            var go = new GameObject(Tag + "-" + name);
            _created.Add(go);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            return go;
        }

        private Container MakeContainer(string id, Vector3 position, float size = 20f)
        {
            var c = Make(id, null, position).AddComponent<Container>();
            c.ContainerId = Tag + "-" + id;
            c.Size = Vector3.one * size;
            c.Center = Vector3.zero;
            return c;
        }

        private NetworkIdentity MakeBody(string name, Transform parent = null, Vector3 position = default)
        {
            var go = Make(name, parent, position);
            var identity = go.AddComponent<NetworkIdentity>();
            go.AddComponent<Rigidbody>();
            return identity;
        }

        private static List<NebulaValidator.Issue> Validate()
        {
            var issues = new List<NebulaValidator.Issue>();
            NebulaValidator.CheckPhysicsIslands(null, issues);
            // The Editor's open scene may hold anything; keep the issues about this fixture's objects.
            return issues.Where(i => i.Message.Contains(Tag)).ToList();
        }

        // ---- editor check ---------------------------------------------------------------------------------

        [Test]
        public void HingeJointAcrossTwoContainersIsReportedNamingBothContainers()
        {
            var a = MakeContainer("a", Vector3.zero);
            var b = MakeContainer("b", new Vector3(100f, 0f, 0f));
            var bodyA = MakeBody("hull", a.transform);
            var bodyB = MakeBody("trailer", b.transform);
            var hinge = bodyA.gameObject.AddComponent<HingeJoint>();
            hinge.connectedBody = bodyB.GetComponent<Rigidbody>();

            var issues = Validate();

            Assert.AreEqual(1, issues.Count, string.Join("\n", issues));
            Assert.AreEqual(NebulaValidator.Severity.Warning, issues[0].Severity);
            StringAssert.Contains("physdiag-a", issues[0].Message);
            StringAssert.Contains("physdiag-b", issues[0].Message);
            StringAssert.Contains("HingeJoint", issues[0].Message);
            StringAssert.Contains("/docs/concepts/distributed-physics", issues[0].Message);
            Assert.AreSame(hinge, issues[0].Context, "the joint is the issue's context so clicking the log line selects it");
        }

        [Test]
        public void JointInsideOneContainerIsNotReported()
        {
            var a = MakeContainer("a", Vector3.zero);
            var bodyA = MakeBody("hull", a.transform);
            var bodyB = MakeBody("trailer", a.transform);
            bodyA.gameObject.AddComponent<FixedJoint>().connectedBody = bodyB.GetComponent<Rigidbody>();

            Assert.IsEmpty(Validate());
        }

        [Test]
        public void JointToTheWorldOrToANonNetworkedBodyIsNotReported()
        {
            var a = MakeContainer("a", Vector3.zero);
            var bodyA = MakeBody("hull", a.transform);
            bodyA.gameObject.AddComponent<SpringJoint>(); // connected to the world
            var prop = Make("crate", null, new Vector3(100f, 0f, 0f)).AddComponent<Rigidbody>();
            bodyA.gameObject.AddComponent<FixedJoint>().connectedBody = prop; // no NetworkIdentity on that side

            Assert.IsEmpty(Validate());
        }

        [Test]
        public void UnparentedEntitiesResolveTheirContainerByPosition()
        {
            MakeContainer("a", Vector3.zero);
            MakeContainer("b", new Vector3(100f, 0f, 0f));
            var bodyA = MakeBody("hull", null, new Vector3(1f, 0f, 0f));
            var bodyB = MakeBody("trailer", null, new Vector3(101f, 0f, 0f));
            bodyA.gameObject.AddComponent<ConfigurableJoint>().connectedBody = bodyB.GetComponent<Rigidbody>();

            var issues = Validate();

            Assert.AreEqual(1, issues.Count, string.Join("\n", issues));
            StringAssert.Contains("physdiag-a", issues[0].Message);
            StringAssert.Contains("physdiag-b", issues[0].Message);
        }

        [Test]
        public void EntityInAContainerJoinedToOneOutsideAnyContainerIsReported()
        {
            MakeContainer("a", Vector3.zero);
            var bodyA = MakeBody("hull", null, new Vector3(1f, 0f, 0f));
            var bodyB = MakeBody("trailer", null, new Vector3(500f, 0f, 0f));
            bodyA.gameObject.AddComponent<CharacterJoint>().connectedBody = bodyB.GetComponent<Rigidbody>();

            var issues = Validate();

            Assert.AreEqual(1, issues.Count, string.Join("\n", issues));
            StringAssert.Contains("physdiag-a", issues[0].Message);
            StringAssert.Contains("no container", issues[0].Message);
        }

        [Test]
        public void JointBetweenACarrierAndItsCarriedEntityIsReported()
        {
            var outdoor = MakeContainer("outdoor", Vector3.zero, 200f);
            var ship = MakeBody("ship", outdoor.transform);
            var interior = ship.gameObject.AddComponent<Container>();
            interior.ContainerId = Tag + "-interior";
            interior.Size = Vector3.one * 10f;
            // At edit time the carried box is the Container on the ship; DynamicContainer only registers it at runtime.
            var crate = MakeBody("crate", ship.transform);
            crate.gameObject.AddComponent<FixedJoint>().connectedBody = ship.GetComponent<Rigidbody>();

            var issues = Validate();

            Assert.AreEqual(1, issues.Count, string.Join("\n", issues));
            StringAssert.Contains("physdiag-interior", issues[0].Message);
            StringAssert.Contains("physdiag-outdoor", issues[0].Message);
        }

        [Test]
        public void ArticulationAcrossContainersIsReportedOnce()
        {
            var a = MakeContainer("a", Vector3.zero);
            var b = MakeContainer("b", new Vector3(100f, 0f, 0f));
            // An articulation's connected body is its parent ArticulationBody in the transform hierarchy, so an
            // articulation across entities means a child entity under a parent entity. The hierarchy therefore
            // resolves both to 'a'; a resolver standing in for the worker's per-entity containers puts the arm in
            // 'b', as a pinned dynamic container or a handover would.
            var root = Make("base", a.transform);
            var baseIdentity = root.AddComponent<NetworkIdentity>();
            root.AddComponent<ArticulationBody>();
            var arm = Make("arm", root.transform);
            var armIdentity = arm.AddComponent<NetworkIdentity>();
            var armBody = arm.AddComponent<ArticulationBody>();
            Make("link", arm.transform).AddComponent<ArticulationBody>(); // inside the arm entity: never reported
            Container Resolve(NetworkIdentity id) => id == armIdentity ? b : a;

            var results = new List<CrossIslandJoint>();
            PhysicsIslands.FindCrossIslandJoints(armIdentity, results, Resolve);
            PhysicsIslands.FindCrossIslandJoints(baseIdentity, results, Resolve);

            Assert.AreEqual(1, results.Count, "the arm's articulation crosses once; the base's own is a root and the link stays inside the arm");
            Assert.AreSame(armBody, results[0].Constraint);
            Assert.AreSame(b, results[0].OwnerContainer);
            Assert.AreSame(a, results[0].ConnectedContainer);
        }

        [Test]
        public void SameIslandTreatsTwoNullContainersAsOneIslandAndNullAgainstAContainerAsTwo()
        {
            var a = MakeContainer("a", Vector3.zero);
            Assert.IsTrue(PhysicsIslands.SameIsland((Container)null, null));
            Assert.IsTrue(PhysicsIslands.SameIsland(a, a));
            Assert.IsFalse(PhysicsIslands.SameIsland(a, null));
        }

        [Test]
        public void IsCohesiveHookExemptsAPairFromTheCheck()
        {
            var a = MakeContainer("a", Vector3.zero);
            var b = MakeContainer("b", new Vector3(100f, 0f, 0f));
            var bodyA = MakeBody("hull", a.transform);
            var bodyB = MakeBody("trailer", b.transform);
            bodyA.gameObject.AddComponent<HingeJoint>().connectedBody = bodyB.GetComponent<Rigidbody>();

            PhysicsIslands.IsCohesive = (x, y) => (x == bodyA && y == bodyB) || (x == bodyB && y == bodyA);
            Assert.IsEmpty(Validate(), "a cohesive pair is one island whatever their containers");

            PhysicsIslands.IsCohesive = null;
            Assert.AreEqual(1, Validate().Count);
        }

        [Test]
        public void JointBetweenEntitiesInOneAuthoredCohesionGroupIsNotReported()
        {
            var a = MakeContainer("a", Vector3.zero);
            var b = MakeContainer("b", new Vector3(100f, 0f, 0f));
            var bodyA = MakeBody("hull", a.transform);
            var bodyB = MakeBody("trailer", b.transform);
            bodyA.gameObject.AddComponent<HingeJoint>().connectedBody = bodyB.GetComponent<Rigidbody>();

            Assert.AreEqual(1, Validate().Count, "without a group the joint crosses two containers");

            // The cohesion group is the game's promise that one worker simulates both (docs/cohesion-hints.md, D6).
            bodyA.JoinCohesionGroup(31);
            Assert.AreEqual(1, Validate().Count, "one side alone is not a group");
            bodyB.JoinCohesionGroup(31);
            Assert.IsEmpty(Validate(), "two members of one cohesion group are one island whatever their containers");

            bodyB.JoinCohesionGroup(32);
            Assert.AreEqual(1, Validate().Count, "two different groups are two islands");
            bodyA.LeaveCohesionGroup();
            bodyB.LeaveCohesionGroup();
            Assert.AreEqual(1, Validate().Count);
        }

        // ---- worker check ---------------------------------------------------------------------------------

        [Test]
        public void WorkerWarnsOncePerEntityWhenItGainsAuthorityOverACrossContainerJoint()
        {
            var a = MakeContainer("a", Vector3.zero);
            var b = MakeContainer("b", new Vector3(100f, 0f, 0f));
            var bodyA = MakeBody("hull");
            var bodyB = MakeBody("trailer");
            bodyA.Initialize(); bodyA.NetId = 0x7001;
            bodyB.Initialize(); bodyB.NetId = 0x7002;
            bodyA.SetContainer(a);
            bodyB.SetContainer(b);
            bodyA.gameObject.AddComponent<HingeJoint>().connectedBody = bodyB.GetComponent<Rigidbody>();

            // Inline (?s) rather than RegexOptions: the message spans two lines and LogAssert keeps only the pattern.
            LogAssert.Expect(LogType.Warning, new Regex(@"(?s)physdiag-hull.*1 joint\(s\) across containers.*physdiag-a.*physdiag-b"));
            PhysicsIslands.CheckOnAuthority(bodyA);
            PhysicsIslands.CheckOnAuthority(bodyA); // a second authority change of the same entity is silent
            PhysicsIslands.CheckOnAuthority(bodyB); // the trailer's own hierarchy holds no joint
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void WorkerCheckIsSilentForAnEntityWithoutConstraintsOrWithASameContainerJoint()
        {
            var a = MakeContainer("a", Vector3.zero);
            var bodyA = MakeBody("hull");
            var bodyB = MakeBody("trailer");
            bodyA.Initialize(); bodyA.NetId = 0x7003;
            bodyB.Initialize(); bodyB.NetId = 0x7004;
            bodyA.SetContainer(a);
            bodyB.SetContainer(a);
            bodyA.gameObject.AddComponent<FixedJoint>().connectedBody = bodyB.GetComponent<Rigidbody>();

            PhysicsIslands.CheckOnAuthority(bodyA);
            PhysicsIslands.CheckOnAuthority(bodyB);
            LogAssert.NoUnexpectedReceived();
        }

        // ---- AuthorityRpc sender check --------------------------------------------------------------------

        private sealed class Target : NetworkBehaviour
        {
            public int Hits;
            public void Claim(float amount) => AuthorityRpc(RpcApplyDamage, amount);

            [AuthorityRpc]
            private void RpcApplyDamage(float amount) => Hits += (int)amount;
        }

        private sealed class RecordingSink : IRpcSink
        {
            public int AuthorityRpcs;
            public void SendClientRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, ulong targetClientId, float radius) { }
            public void SendServerRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args) { }
            public void SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args) => AuthorityRpcs++;
            public ulong SendAuthorityRpc(NetworkIdentity identity, byte behaviourIndex, uint methodHash, ArraySegment<byte> args, Action<AuthorityCallResult> onDone, float timeoutSeconds) { AuthorityRpcs++; return 1; }
        }

        [Test]
        public void AuthorityRpcFromACopyThatIsNeitherAuthoritativeNorGhostIsRejectedAndCounted()
        {
            var go = Make("target");
            var identity = go.AddComponent<NetworkIdentity>();
            var target = go.AddComponent<Target>();
            identity.Initialize();
            identity.NetId = 0x7005;
            identity.IsSpawned = true;
            var sink = new RecordingSink();
            NebulaRuntime.RpcSink = sink;

            // A client copy: not a worker, so neither authoritative nor a ghost.
            NebulaRuntime.IsServer = false;
            NebulaRuntime.IsClient = true;
            identity.HasAuthority = false;
            int before = NebulaDiagnostics.RejectedAuthorityRpcSends;
            LogAssert.Expect(LogType.Warning, new Regex("AuthorityRpc RpcApplyDamage on Target .*neither an authoritative nor a ghost copy"));
            target.Claim(5f);
            Assert.AreEqual(before + 1, NebulaDiagnostics.RejectedAuthorityRpcSends, "the rejected send is counted");
            Assert.AreEqual(0, sink.AuthorityRpcs, "nothing reaches the sink");
            Assert.AreEqual(0, target.Hits, "nothing runs locally");

            // A ghost on a worker: allowed, goes to the owner's worker.
            NebulaRuntime.IsServer = true;
            NebulaRuntime.IsClient = false;
            target.Claim(5f);
            Assert.AreEqual(1, sink.AuthorityRpcs);
            Assert.AreEqual(before + 1, NebulaDiagnostics.RejectedAuthorityRpcSends, "a ghost's send is not a rejection");

            // The authority: runs locally.
            identity.HasAuthority = true;
            target.Claim(5f);
            Assert.AreEqual(5, target.Hits);
            Assert.AreEqual(1, sink.AuthorityRpcs);
            Assert.AreEqual(before + 1, NebulaDiagnostics.RejectedAuthorityRpcSends);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void AuthorityRpcOnAnUnspawnedEntityIsRejectedAndCounted()
        {
            var go = Make("target");
            var identity = go.AddComponent<NetworkIdentity>();
            var target = go.AddComponent<Target>();
            identity.Initialize();
            NebulaRuntime.IsServer = true;
            NebulaRuntime.RpcSink = new RecordingSink();
            int before = NebulaDiagnostics.RejectedAuthorityRpcSends;
            LogAssert.Expect(LogType.Warning, new Regex("RPC RpcApplyDamage on unspawned Target ignored"));
            target.Claim(1f);
            Assert.AreEqual(before + 1, NebulaDiagnostics.RejectedAuthorityRpcSends);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
