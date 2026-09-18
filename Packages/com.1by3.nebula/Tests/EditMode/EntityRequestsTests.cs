using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Nebula.Tests
{
    public class EntityRequestsTests
    {
        /// <summary>Workers wired together in memory: a send queues bytes at the target until <see cref="Deliver"/>. Copied from WorkerQueryTests' fake mesh.</summary>
        private sealed class FakeMesh
        {
            public readonly Dictionary<ushort, FakeWorker> Workers = new Dictionary<ushort, FakeWorker>();
            public float Now;

            public FakeWorker Add(ushort index)
            {
                var w = new FakeWorker(this, index);
                Workers[index] = w;
                return w;
            }

            public IEnumerable<ushort> ConnectedTo(ushort self)
            {
                foreach (var w in Workers.Values)
                    if (w.WorkerIndex != self && w.Connected) yield return w.WorkerIndex;
            }

            /// <summary>Hand every queued message to its handler, including those queued while delivering.</summary>
            public int Deliver()
            {
                int delivered = 0;
                bool any;
                do
                {
                    any = false;
                    foreach (var w in Workers.Values)
                        while (w.Inbox.Count > 0)
                        {
                            var m = w.Inbox.Dequeue();
                            any = true;
                            delivered++;
                            if (w.Handlers.TryGetValue(m.Kind, out var h)) h($"w{m.From}", m.From, new NetworkReader(m.Bytes));
                        }
                } while (any);
                return delivered;
            }
        }

        private sealed class FakeWorker : IWorkerMessaging
        {
            public struct Message { public ushort From, Kind; public byte[] Bytes; }

            private readonly FakeMesh _mesh;
            public readonly Dictionary<ushort, NebulaWorker.WorkerMessageHandler> Handlers = new Dictionary<ushort, NebulaWorker.WorkerMessageHandler>();
            public readonly Queue<Message> Inbox = new Queue<Message>();
            public bool Connected = true;
            private readonly NetworkWriter _writer = new NetworkWriter();

            public FakeWorker(FakeMesh mesh, ushort index) { _mesh = mesh; WorkerIndex = index; }

            public ushort WorkerIndex { get; }
            public void RegisterMessageHandler(ushort kind, NebulaWorker.WorkerMessageHandler handler) => Handlers[kind] = handler;
            public void UnregisterMessageHandler(ushort kind) => Handlers.Remove(kind);

            public bool SendToWorker(ushort workerIndex, ushort kind, Action<NetworkWriter> write, Delivery delivery = Delivery.ReliableOrdered)
            {
                _writer.Reset();
                write(_writer);
                var bytes = _writer.ToArray();
                if (workerIndex == WorkerIndex)
                {
                    if (!Handlers.TryGetValue(kind, out var h)) return false;
                    h($"w{WorkerIndex}", WorkerIndex, new NetworkReader(bytes));
                    return true;
                }
                if (!_mesh.Workers.TryGetValue(workerIndex, out var target) || !target.Connected || !Connected) return false;
                target.Inbox.Enqueue(new Message { From = WorkerIndex, Kind = kind, Bytes = bytes });
                return true;
            }
        }

        private const ushort Kind = 1;
        private readonly List<NetworkIdentity> _spawned = new List<NetworkIdentity>();

        [TearDown]
        public void TearDown()
        {
            foreach (var i in _spawned) if (i != null) UnityEngine.Object.DestroyImmediate(i.gameObject);
            _spawned.Clear();
        }

        private NetworkIdentity MakeAuthoritative(string key)
        {
            var go = new GameObject("entity_" + key);
            var identity = go.AddComponent<NetworkIdentity>();
            identity.HasAuthority = true;
            _spawned.Add(identity);
            return identity;
        }

        /// <summary>Only <paramref name="ownerIndex"/> resolves <paramref name="key"/> to an authoritative identity; every other worker resolves null.</summary>
        private static EntityRequests Make(FakeMesh mesh, FakeWorker w, string key, ushort ownerIndex, NetworkIdentity owned, EntityRequests.Handler handler)
        {
            var er = new EntityRequests(w, k => (k == key && w.WorkerIndex == ownerIndex) ? owned : null, () => mesh.ConnectedTo(w.WorkerIndex), () => mesh.Now);
            if (w.WorkerIndex == ownerIndex && handler != null) er.RegisterHandler(Kind, handler);
            return er;
        }

        [Test]
        public void LocalAuthorityAnswersWithNoNetworkTraffic()
        {
            var mesh = new FakeMesh();
            var w1 = mesh.Add(1);
            var entity = MakeAuthoritative("chest-1");
            var er1 = Make(mesh, w1, "chest-1", 1, entity, (e, payload, reply) => reply(new byte[] { (byte)(payload.Array[payload.Offset] + 1) }));

            EntityRequestResult? result = null;
            uint id = er1.Request("chest-1", Kind, new byte[] { 41 }, r => result = r, 1f);
            Assert.AreNotEqual(0u, id);
            Assert.IsNotNull(result, "the owning worker answers inline, before Request returns");
            Assert.AreEqual(EntityRequestStatus.Replied, result.Value.Status);
            Assert.AreEqual(42, result.Value.Reply[0]);
            Assert.AreEqual(0, er1.PendingCount);
            Assert.AreEqual(0, mesh.Deliver(), "nothing was sent over the wire");
        }

        [Test]
        public void RemoteAuthorityAnswersAfterADeferredReply()
        {
            var mesh = new FakeMesh();
            var w1 = mesh.Add(1); var w2 = mesh.Add(2); var w3 = mesh.Add(3);
            var entity = MakeAuthoritative("boss-1");
            Action<byte[]> heldReply = null;
            var er1 = Make(mesh, w1, "boss-1", 2, null, null);
            var er2 = Make(mesh, w2, "boss-1", 2, entity, (e, payload, reply) => heldReply = reply); // holds the reply open
            var er3 = Make(mesh, w3, "boss-1", 2, null, null);

            EntityRequestResult? result = null;
            er1.Request("boss-1", Kind, Array.Empty<byte>(), r => result = r, 5f);
            mesh.Deliver(); // requests reach w2 and w3; w3 declines at once
            Assert.IsNull(result, "w2 has not replied yet");
            Assert.IsNotNull(heldReply, "w2's handler ran and is holding the reply");
            Assert.AreEqual(1, er1.PendingCount);

            mesh.Now = 2f; // several ticks pass before the handler is ready
            heldReply(new byte[] { 7 });
            mesh.Deliver();
            Assert.IsNotNull(result);
            Assert.AreEqual(EntityRequestStatus.Replied, result.Value.Status);
            Assert.AreEqual(7, result.Value.Reply[0]);
            Assert.AreEqual(0, er1.PendingCount);
        }

        [Test]
        public void EveryoneDecliningTwiceConcludesNotFound()
        {
            var mesh = new FakeMesh();
            var w1 = mesh.Add(1); var w2 = mesh.Add(2); var w3 = mesh.Add(3);
            var er1 = Make(mesh, w1, "ghost-key", 99, null, null); // 99: nobody owns it
            Make(mesh, w2, "ghost-key", 99, null, null);
            Make(mesh, w3, "ghost-key", 99, null, null);

            EntityRequestResult? result = null;
            er1.Request("ghost-key", Kind, Array.Empty<byte>(), r => result = r, 5f);
            mesh.Deliver(); // both decline immediately
            Assert.IsNull(result, "the first round is retried before giving up");
            Assert.AreEqual(1, er1.PendingCount);

            mesh.Now = EntityRequests.RetryDelaySeconds; // let the retry fire
            er1.Update();
            mesh.Deliver(); // second round also declines
            Assert.IsNotNull(result);
            Assert.AreEqual(EntityRequestStatus.NotFound, result.Value.Status);
            Assert.AreEqual(0, er1.PendingCount);
        }

        [Test]
        public void NoReplyBeforeTheDeadlineTimesOut()
        {
            var mesh = new FakeMesh();
            var w1 = mesh.Add(1); var w2 = mesh.Add(2);
            var er1 = Make(mesh, w1, "silent-key", 2, null, null);
            Make(mesh, w2, "silent-key", 2, MakeAuthoritative("silent-key"), (e, payload, reply) => { /* never replies */ });

            EntityRequestResult? result = null;
            er1.Request("silent-key", Kind, Array.Empty<byte>(), r => result = r, 0.5f);
            mesh.Deliver(); // w2 claims it but holds the reply open forever
            Assert.IsNull(result);
            Assert.AreEqual(1, er1.PendingCount);

            mesh.Now = 0.4f;
            er1.Update();
            Assert.IsNull(result);

            mesh.Now = 0.5f;
            er1.Update();
            Assert.IsNotNull(result);
            Assert.AreEqual(EntityRequestStatus.TimedOut, result.Value.Status);
            Assert.AreEqual(0, er1.PendingCount);
        }

        [Test]
        public void ADisconnectedWorkerCannotHangARequestForever()
        {
            var mesh = new FakeMesh();
            var w1 = mesh.Add(1); var w2 = mesh.Add(2);
            var er1 = Make(mesh, w1, "somewhere", 2, null, null);
            Make(mesh, w2, "somewhere", 2, null, null);

            EntityRequestResult? result = null;
            er1.Request("somewhere", Kind, Array.Empty<byte>(), r => result = r, 1f);
            w2.Connected = false; // vanishes before it can even decline
            mesh.Deliver();
            Assert.IsNull(result);

            mesh.Now = 1f;
            er1.Update();
            Assert.IsNotNull(result, "bounded by the timeout even though nobody ever answered");
            Assert.AreEqual(EntityRequestStatus.TimedOut, result.Value.Status);
        }

        [Test]
        public void ReplyAfterCancelIsIgnored()
        {
            var mesh = new FakeMesh();
            var w1 = mesh.Add(1); var w2 = mesh.Add(2);
            var entity = MakeAuthoritative("cancel-key");
            Action<byte[]> heldReply = null;
            var er1 = Make(mesh, w1, "cancel-key", 2, null, null);
            Make(mesh, w2, "cancel-key", 2, entity, (e, payload, reply) => heldReply = reply);

            int calls = 0;
            uint id = er1.Request("cancel-key", Kind, Array.Empty<byte>(), r => calls++, 5f);
            mesh.Deliver();
            Assert.IsTrue(er1.Cancel(id));
            heldReply(Array.Empty<byte>());
            mesh.Deliver();
            Assert.AreEqual(0, calls);
        }

        [Test]
        public void ASecondReplyIsIgnored()
        {
            var mesh = new FakeMesh();
            var w1 = mesh.Add(1);
            var entity = MakeAuthoritative("double-reply");
            int calls = 0;
            var er1 = Make(mesh, w1, "double-reply", 1, entity, (e, payload, reply) =>
            {
                reply(new byte[] { 1 });
                reply(new byte[] { 2 }); // must be ignored, not throw
            });

            er1.Request("double-reply", Kind, Array.Empty<byte>(), r => calls++, 1f);
            Assert.AreEqual(1, calls);
        }
    }
}
