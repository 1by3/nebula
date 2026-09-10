using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    public class ControlPlaneAndRpcTests
    {
        [Test]
        public void LocalControlPlaneLeaseSemanticsMatchTheModule()
        {
            var cp = new LocalControlPlane();
            cp.Connect();
            int changes = 0;
            cp.Changed += () => changes++;
            cp.EnsureContainer("c1");
            cp.Tick();
            Assert.AreEqual(1, changes);
            var lease = cp.FindLease("c1");
            Assert.AreEqual(LeaseState.Orphaned, lease.State);
            Assert.AreEqual(0UL, lease.Epoch);

            cp.AssignContainer("c1", "w1");
            Assert.AreEqual(1UL, cp.FindLease("c1").Epoch);
            cp.AssignContainer("c1", "w1"); // no-op must not burn an epoch
            Assert.AreEqual(1UL, cp.FindLease("c1").Epoch);
            cp.AssignContainer("c1", "w2");
            Assert.AreEqual(2UL, cp.FindLease("c1").Epoch);
            Assert.AreEqual("w2", cp.FindLease("c1").WorkerId);

            cp.RegisterWorker("w2", 2, "127.0.0.1", 7102);
            Assert.IsTrue(cp.IsWorkerAlive(cp.FindWorker("w2"), 5f));
            cp.UnregisterWorker("w2");
            Assert.IsNull(cp.FindWorker("w2"));
            Assert.AreEqual(LeaseState.Orphaned, cp.FindLease("c1").State);
            Assert.AreEqual("", cp.FindLease("c1").WorkerId);
        }

        private sealed class Dummy : NetworkBehaviour
        {
            public int Received;
            public string Text;

            [ClientRpc]
            private void RpcHello(int n, string s) { Received = n; Text = s; }

            [AuthorityRpc]
            private void TakeHit(float amount) { Received = (int)amount; }
        }

        [Test]
        public void RpcRegistryFindsAttributedMethodsAndInvokesThemWithDeserialisedArguments()
        {
            var go = new GameObject("dummy");
            try
            {
                go.AddComponent<NetworkIdentity>();
                var d = go.AddComponent<Dummy>();
                var m = RpcRegistry.Require(typeof(Dummy), "RpcHello");
                Assert.AreEqual(2, m.ParameterTypes.Length);
                var w = new NetworkWriter();
                RpcRegistry.WriteArgs(w, m, new object[] { 42, "hi" });
                RpcRegistry.Invoke(d, m.Hash, new NetworkReader(w.ToSegment()));
                Assert.AreEqual(42, d.Received);
                Assert.AreEqual("hi", d.Text);
                Assert.IsNotNull(RpcRegistry.Lookup(typeof(Dummy), RpcRegistry.Hash("TakeHit")));
                Assert.IsNull(RpcRegistry.Lookup(typeof(Dummy), RpcRegistry.Hash("NotAnRpc")));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private sealed class WithVars : NetworkBehaviour
        {
            public NetworkVariable<int> Score = new NetworkVariable<int>(3);
            public NetworkVariable<string> Name;
            private NetworkVariable<float> _hidden = new NetworkVariable<float>(1.5f);
            public float Hidden => _hidden.Value;
        }

        private sealed class Spinning : NetworkBehaviour
        {
            public Vector3 Spin = new Vector3(1, 2, 3);
            public Vector3 Received;
            public override void WriteHandoverState(NetworkWriter writer) => writer.WriteVector3(Spin);
            public override void ReadHandoverState(NetworkReader reader) => Received = reader.ReadVector3();
        }

        private sealed class Greedy : NetworkBehaviour
        {
            public bool Threw;
            public override void ReadHandoverState(NetworkReader reader)
            {
                try { reader.ReadUInt(); } catch (System.InvalidOperationException) { Threw = true; throw; }
            }
        }

        [Test]
        public void HandoverStateRoundTripsPerBehaviourAndIsolatesFaultyChunks()
        {
            var a = new GameObject("a");
            var b = new GameObject("b");
            try
            {
                // Sender: a silent behaviour, then a spinning one, then a silent one.
                var ia = a.AddComponent<NetworkIdentity>();
                a.AddComponent<WithVars>();
                var sa = a.AddComponent<Spinning>();
                a.AddComponent<Dummy>();
                ia.Initialize();
                var w = new NetworkWriter();
                ia.WriteHandoverState(w);

                // Receiver with the same layout gets the spin; the empty chunks read nothing.
                var ib = b.AddComponent<NetworkIdentity>();
                b.AddComponent<WithVars>();
                var sb = b.AddComponent<Spinning>();
                b.AddComponent<Dummy>();
                ib.Initialize();
                var r = new NetworkReader(w.ToSegment());
                ib.ReadHandoverState(r);
                Assert.AreEqual(sa.Spin, sb.Received);
                Assert.AreEqual(0, r.Remaining);

                // A behaviour that reads past its (empty) chunk throws inside the chunk (it cannot reach the next
                // behaviour's bytes); the error is logged and the following behaviour still gets its data.
                var c = new GameObject("c");
                try
                {
                    var ic = c.AddComponent<NetworkIdentity>();
                    var greedy = c.AddComponent<Greedy>();
                    var sc = c.AddComponent<Spinning>();
                    c.AddComponent<Dummy>();
                    ic.Initialize();
                    UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("ReadHandoverState on Greedy"));
                    ic.ReadHandoverState(new NetworkReader(w.ToSegment()));
                    Assert.IsTrue(greedy.Threw);
                    Assert.AreEqual(sa.Spin, sc.Received);
                }
                finally { Object.DestroyImmediate(c); }
            }
            finally
            {
                Object.DestroyImmediate(a);
                Object.DestroyImmediate(b);
            }
        }

        [Test]
        public void NetworkVariablesAreDiscoveredInDeclarationOrderAndRoundTrip()
        {
            var a = new GameObject("a");
            var b = new GameObject("b");
            try
            {
                var ia = a.AddComponent<NetworkIdentity>();
                var va = a.AddComponent<WithVars>();
                ia.Initialize();
                Assert.AreEqual(3, ia.AllVars.Length);
                Assert.IsNotNull(va.Name, "null NetworkVariable fields are created automatically");
                ia.HasAuthority = true;
                va.Score.Value = 10;
                va.Name.Value = "zed";
                Assert.IsTrue(ia.VarsDirty);
                var w = new NetworkWriter();
                ia.WriteVars(w);

                var ib = b.AddComponent<NetworkIdentity>();
                var vb = b.AddComponent<WithVars>();
                ib.Initialize();
                int changed = 0;
                vb.Score.OnValueChanged += (o, n) => changed++;
                ib.ReadVars(new NetworkReader(w.ToSegment()));
                Assert.AreEqual(10, vb.Score.Value);
                Assert.AreEqual("zed", vb.Name.Value);
                Assert.AreEqual(1.5f, vb.Hidden);
                Assert.AreEqual(1, changed);
            }
            finally
            {
                Object.DestroyImmediate(a);
                Object.DestroyImmediate(b);
            }
        }

        [Test]
        public void RemoteInterpolatorInterpolatesBetweenSamplesAndExtrapolatesPastTheNewest()
        {
            var go = new GameObject("interp");
            try
            {
                var it = go.AddComponent<RemoteInterpolator>();
                it.Push(100, new Vector3(0, 0, 0), Quaternion.identity, new Vector3(60, 0, 0));
                it.Push(102, new Vector3(2, 0, 0), Quaternion.identity, new Vector3(60, 0, 0));
                Assert.IsTrue(it.Sample(101.0, out var p, out _));
                Assert.AreEqual(1f, p.x, 0.001f);
                Assert.IsTrue(it.Sample(103.0, out p, out _));
                Assert.AreEqual(3f, p.x, 0.001f, "one tick past the newest sample at 60 m/s is one metre further");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
