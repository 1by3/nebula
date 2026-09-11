using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Nebula.Tests
{
    /// <summary>NetworkTransform / NetworkAnimator and the sync channel they ride on, exercised in-process.</summary>
    public class SyncComponentTests
    {
        private static readonly NetworkWriter Writer = new NetworkWriter(4096);

        [SetUp]
        public void SetUp()
        {
            NebulaRuntime.Reset();
            NebulaRuntime.IsServer = true;
        }

        [TearDown]
        public void TearDown() => NebulaRuntime.Reset();

        /// <summary>An authoritative entity with a child NetworkTransform, and a non-authoritative copy of the same prefab.</summary>
        private static (NetworkIdentity id, NetworkTransform nt, Transform child) MakeEntity(string name, bool authority, System.Action<NetworkTransform> configure = null)
        {
            var root = new GameObject(name);
            var id = root.AddComponent<NetworkIdentity>();
            var childGo = new GameObject("child");
            childGo.transform.SetParent(root.transform, false);
            var nt = childGo.AddComponent<NetworkTransform>();
            configure?.Invoke(nt);
            id.Initialize();
            id.HasAuthority = authority;
            id.IsSpawned = true;
            id.InvokeSpawn();
            if (authority) id.SetAuthority(true);
            return (id, nt, childGo.transform);
        }

        private static byte[] Stream(NetworkIdentity from, uint tick, Delivery delivery)
        {
            Writer.Reset();
            int n = from.WriteSyncState(Writer, tick, delivery);
            var bytes = n > 0 ? Writer.ToArray() : null;
            from.ClearDirty();
            return bytes;
        }

        private static void Deliver(NetworkIdentity to, byte[] chunks, uint tick)
        {
            Assert.IsNotNull(chunks, "expected a sync message this tick");
            to.ReadSyncState(new NetworkReader(chunks), tick, null);
        }

        [Test]
        public void ChildTransformReplicatesSelectedAxesAndSnapsWithoutInterpolation()
        {
            var (a, ntA, childA) = MakeEntity("auth", true, nt => { nt.SyncPositionY = false; nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false; });
            var (b, ntB, childB) = MakeEntity("copy", false, nt => { nt.SyncPositionY = false; nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false; nt.Interpolate = false; });
            try
            {
                childA.localPosition = new Vector3(1f, 5f, -2f);
                childA.localRotation = Quaternion.Euler(0f, 90f, 0f);
                ntA.NetworkTick(10, NetworkTime.TickInterval);
                Assert.IsTrue(a.SyncDirty, "a moved child marks the identity dirty");
                Deliver(b, Stream(a, 10, Delivery.ReliableOrdered), 10);
                Assert.AreEqual(1f, childB.localPosition.x, 1e-4f);
                Assert.AreEqual(-2f, childB.localPosition.z, 1e-4f);
                Assert.AreEqual(0f, childB.localPosition.y, 1e-4f, "Y is not synchronised");
                Assert.AreEqual(90f, Quaternion.Angle(Quaternion.identity, childB.localRotation), 0.01f);

                // Below the thresholds nothing is sent.
                childA.localPosition += new Vector3(0.0001f, 0f, 0f);
                ntA.NetworkTick(11, NetworkTime.TickInterval);
                Assert.IsNull(Stream(a, 11, Delivery.ReliableOrdered), "a sub-threshold move sends nothing");
            }
            finally
            {
                Object.DestroyImmediate(a.gameObject);
                Object.DestroyImmediate(b.gameObject);
            }
        }

        [Test]
        public void BufferedInterpolationSamplesBetweenTicks()
        {
            var (a, ntA, childA) = MakeEntity("auth", true);
            var (b, ntB, childB) = MakeEntity("copy", false);
            try
            {
                childA.localPosition = Vector3.zero;
                ntA.NetworkTick(100, NetworkTime.TickInterval);
                Deliver(b, Stream(a, 100, Delivery.ReliableOrdered), 100);
                childA.localPosition = new Vector3(4f, 0f, 0f);
                ntA.NetworkTick(102, NetworkTime.TickInterval);
                Deliver(b, Stream(a, 102, Delivery.ReliableOrdered), 102);

                b.RemoteTick(101.0);
                Assert.AreEqual(2f, childB.localPosition.x, 1e-3f, "halfway between the tick-100 and tick-102 samples");
                b.RemoteTick(101.5);
                Assert.AreEqual(3f, childB.localPosition.x, 1e-3f);
                b.RemoteTick(110.0);
                Assert.AreEqual(4f, childB.localPosition.x, 1e-3f, "past the newest sample it holds");
            }
            finally
            {
                Object.DestroyImmediate(a.gameObject);
                Object.DestroyImmediate(b.gameObject);
            }
        }

        [Test]
        public void HalfFloatsAndCompressedQuaternionsStayWithinTolerance()
        {
            System.Action<NetworkTransform> compact = nt =>
            {
                nt.UseHalfFloatPrecision = true;
                nt.UseQuaternionSynchronization = true;
                nt.UseQuaternionCompression = true;
                nt.Interpolate = false;
            };
            var (a, ntA, childA) = MakeEntity("auth", true, compact);
            var (b, ntB, childB) = MakeEntity("copy", false, compact);
            try
            {
                childA.localPosition = new Vector3(12.5f, -3.25f, 7.125f);
                childA.localRotation = Quaternion.Euler(33f, -120f, 71f);
                childA.localScale = new Vector3(2f, 0.5f, 1f);
                ntA.NetworkTick(1, NetworkTime.TickInterval);
                var bytes = Stream(a, 1, Delivery.ReliableOrdered);
                // envelope(1) + index(1) + flags(1) + len(2) + fields(1) + pos(3 halves) + rot(4) + scale(3 halves)
                Assert.AreEqual(1 + 1 + 1 + 2 + 1 + 6 + 4 + 6, bytes.Length);
                Deliver(b, bytes, 1);
                Assert.AreEqual(12.5f, childB.localPosition.x, 0.02f);
                Assert.AreEqual(-3.25f, childB.localPosition.y, 0.02f);
                Assert.AreEqual(7.125f, childB.localPosition.z, 0.02f);
                Assert.Less(Quaternion.Angle(childA.localRotation, childB.localRotation), 0.2f, "smallest-three keeps rotations within a fifth of a degree");
                Assert.AreEqual(0.5f, childB.localScale.y, 0.01f);
            }
            finally
            {
                Object.DestroyImmediate(a.gameObject);
                Object.DestroyImmediate(b.gameObject);
            }
        }

        [Test]
        public void CompressedQuaternionRoundTripsEveryOctant()
        {
            var w = new NetworkWriter();
            var rng = new System.Random(7);
            for (int i = 0; i < 500; i++)
            {
                var q = Quaternion.Euler((float)rng.NextDouble() * 360f, (float)rng.NextDouble() * 360f, (float)rng.NextDouble() * 360f);
                w.Reset();
                w.WriteCompressedQuaternion(q);
                var back = new NetworkReader(w.ToSegment()).ReadCompressedQuaternion();
                Assert.Less(Quaternion.Angle(q, back), 0.2f, $"sample {i}");
            }
        }

        [Test]
        public void UnreliableTransformsGetKeyframesOnTheIntervalEvenWhenIdle()
        {
            var (a, ntA, childA) = MakeEntity("auth", true, nt => nt.UseUnreliableDeltas = true);
            try
            {
                Assert.AreEqual(Delivery.Sequenced, ntA.SyncDelivery);
                ntA.NetworkTick(1, NetworkTime.TickInterval);
                Assert.IsNotNull(Stream(a, 1, Delivery.Sequenced), "first send is a keyframe");
                Assert.IsNull(Stream(a, 1, Delivery.ReliableOrdered), "nothing on the other channel");
                ntA.NetworkTick(2, NetworkTime.TickInterval);
                Assert.IsNull(Stream(a, 2, Delivery.Sequenced), "idle: no delta");
                ntA.NetworkTick(NetworkIdentity.SyncKeyframeInterval, NetworkTime.TickInterval);
                var key = Stream(a, NetworkIdentity.SyncKeyframeInterval, Delivery.Sequenced);
                Assert.IsNotNull(key, "keyframe tick: sent although idle");
                bool full = false;
                SyncStateCodec.ReadEnvelope(new NetworkReader(key), (idx, flags, chunk) => full = (flags & SyncStateCodec.ChunkFlags.Full) != 0);
                Assert.IsTrue(full);
            }
            finally
            {
                Object.DestroyImmediate(a.gameObject);
            }
        }

        [Test]
        public void EveryDestinationWrittenInTheFirstTickAfterGainingAuthorityGetsAKeyframe()
        {
            // The worker writes the same tick once per gateway and once per ghost-holding worker; ClearDirty runs
            // after all of them. Pick a tick that is not on the keyframe interval so only "first send" can make it Full.
            uint tick = NetworkIdentity.SyncKeyframeInterval + 1;
            var (a, ntA, childA) = MakeEntity("auth", true);
            try
            {
                Assert.AreEqual(Delivery.ReliableOrdered, ntA.SyncDelivery);
                ntA.NetworkTick(tick, NetworkTime.TickInterval);

                Writer.Reset();
                Assert.AreEqual(1, a.WriteSyncState(Writer, tick, Delivery.ReliableOrdered), "first destination");
                var first = Writer.ToArray();
                Writer.Reset();
                Assert.AreEqual(1, a.WriteSyncState(Writer, tick, Delivery.ReliableOrdered), "second destination, same tick");
                var second = Writer.ToArray();
                a.ClearDirty();

                Assert.IsTrue(IsFull(first), "first destination gets a keyframe");
                Assert.IsTrue(IsFull(second), "second destination gets a keyframe too");
                CollectionAssert.AreEqual(first, second, "both destinations get identical chunks");

                // The stream is now open: a later dirty tick off the interval is a delta.
                childA.localPosition = new Vector3(1f, 0f, 0f);
                ntA.NetworkTick(tick + 1, NetworkTime.TickInterval);
                var delta = Stream(a, tick + 1, Delivery.ReliableOrdered);
                Assert.IsNotNull(delta);
                Assert.IsFalse(IsFull(delta), "after ClearDirty the next send is a delta");
            }
            finally
            {
                Object.DestroyImmediate(a.gameObject);
            }
        }

        private static bool IsFull(byte[] envelope)
        {
            bool full = false;
            SyncStateCodec.ReadEnvelope(new NetworkReader(envelope), (idx, flags, chunk) => full = (flags & SyncStateCodec.ChunkFlags.Full) != 0);
            return full;
        }

        [Test]
        public void SpawnSnapshotCarriesTheSyncStateAndTeleportSnapsAnInterpolatingCopy()
        {
            var (a, ntA, childA) = MakeEntity("auth", true);
            var (b, ntB, childB) = MakeEntity("copy", false);
            try
            {
                childA.localPosition = new Vector3(0f, 3f, 0f);
                var spawn = EntitySpawnMsg.From(a, new NetworkWriter());
                Assert.IsTrue(spawn.State.Length > 0);
                Writer.Reset();
                spawn.Write(Writer, MsgId.EntitySpawn);
                var r = new NetworkReader(Writer.ToSegment());
                r.ReadByte();
                var back = EntitySpawnMsg.Read(r);
                b.ReadSyncState(new NetworkReader(back.State), 0, null);
                Assert.AreEqual(3f, childB.localPosition.y, 1e-4f, "a tick-0 snapshot is applied at once");

                ntA.Teleport(new Vector3(50f, 0f, 0f), Quaternion.identity, Vector3.one);
                Deliver(b, Stream(a, 5, Delivery.ReliableOrdered), 5);
                Assert.AreEqual(50f, childB.localPosition.x, 1e-4f, "teleport bypasses the interpolation buffer");
            }
            finally
            {
                Object.DestroyImmediate(a.gameObject);
                Object.DestroyImmediate(b.gameObject);
            }
        }

        [Test]
        public void EntitySyncMsgRoundTrips()
        {
            var msg = new EntitySyncMsg { NetId = 42, Epoch = 3, Tick = 1000, Container = new ContainerRef(2), Reliable = false, Chunks = new byte[] { 1, 2, 3 } };
            Writer.Reset();
            msg.Write(Writer, MsgId.EntityState);
            var r = new NetworkReader(Writer.ToSegment());
            Assert.AreEqual(MsgId.EntityState, (MsgId)r.ReadByte());
            var back = EntitySyncMsg.Read(r);
            Assert.AreEqual(42UL, back.NetId);
            Assert.AreEqual(3U, back.Epoch);
            Assert.AreEqual(1000U, back.Tick);
            Assert.AreEqual(new ContainerRef(2), back.Container);
            Assert.AreEqual(Delivery.Sequenced, back.Delivery);
            CollectionAssert.AreEqual(msg.Chunks, back.Chunks);
        }

        // ---- animator ---------------------------------------------------------------------------------------

        private static AnimatorController MakeController()
        {
            var c = new AnimatorController();
            c.AddLayer("Base");
            c.AddParameter("Speed", AnimatorControllerParameterType.Float);
            c.AddParameter("Armed", AnimatorControllerParameterType.Bool);
            c.AddParameter("Weapon", AnimatorControllerParameterType.Int);
            c.AddParameter("Jump", AnimatorControllerParameterType.Trigger);
            var sm = c.layers[0].stateMachine;
            var idle = sm.AddState("Idle");
            var run = sm.AddState("Run");
            var t = idle.AddTransition(run);
            t.AddCondition(AnimatorConditionMode.Greater, 0.5f, "Speed");
            t.hasExitTime = false;
            t.duration = 0f;
            return c;
        }

        private static (NetworkIdentity id, NetworkAnimator na, Animator anim) MakeAnimated(string name, bool authority, AnimatorController controller)
        {
            var go = new GameObject(name);
            var id = go.AddComponent<NetworkIdentity>();
            var anim = go.AddComponent<Animator>();
            anim.runtimeAnimatorController = controller;
            var na = go.AddComponent<NetworkAnimator>();
            id.Initialize();
            id.HasAuthority = authority;
            id.IsSpawned = true;
            anim.Update(0f);
            id.InvokeSpawn();
            if (authority) id.SetAuthority(true);
            return (id, na, anim);
        }

        [Test]
        public void AnimatorParametersTriggersAndStatesReplicate()
        {
            var controller = MakeController();
            var (a, naA, animA) = MakeAnimated("auth", true, controller);
            var (b, naB, animB) = MakeAnimated("copy", false, controller);
            try
            {
                animA.SetFloat("Speed", 0.9f);
                animA.SetBool("Armed", true);
                animA.SetInteger("Weapon", 3);
                naA.SetTrigger("Jump");
                animA.Update(NetworkTime.TickInterval); // Idle -> Run on Speed > 0.5
                naA.NetworkTick(1, NetworkTime.TickInterval);
                Deliver(b, Stream(a, 1, Delivery.ReliableOrdered), 1);

                Assert.AreEqual(0.9f, animB.GetFloat("Speed"), 1e-4f);
                Assert.IsTrue(animB.GetBool("Armed"));
                Assert.AreEqual(3, animB.GetInteger("Weapon"));
                Assert.IsTrue(animB.GetBool("Jump"), "the trigger arrived and is set on the copy");
                animB.Update(0f);
                Assert.AreEqual(animA.GetCurrentAnimatorStateInfo(0).fullPathHash, animB.GetCurrentAnimatorStateInfo(0).fullPathHash, "the copy was moved into the Run state");

                // Idle tick: nothing to send.
                naA.NetworkTick(2, NetworkTime.TickInterval);
                Assert.IsNull(Stream(a, 2, Delivery.ReliableOrdered));

                // A float below the threshold does not send; above it does, and only that parameter.
                animA.SetFloat("Speed", 0.9004f);
                naA.NetworkTick(3, NetworkTime.TickInterval);
                Assert.IsNull(Stream(a, 3, Delivery.ReliableOrdered));
                animA.SetFloat("Speed", 0.2f);
                naA.NetworkTick(4, NetworkTime.TickInterval);
                var delta = Stream(a, 4, Delivery.ReliableOrdered);
                Assert.IsNotNull(delta);
                Deliver(b, delta, 4);
                Assert.AreEqual(0.2f, animB.GetFloat("Speed"), 1e-4f);
            }
            finally
            {
                Object.DestroyImmediate(a.gameObject);
                Object.DestroyImmediate(b.gameObject);
                Object.DestroyImmediate(controller);
            }
        }
    }
}
