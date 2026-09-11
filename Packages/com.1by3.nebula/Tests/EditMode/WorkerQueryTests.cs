using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Nebula.Tests
{
    public class WorkerQueryTests
    {
        /// <summary>Workers wired together in memory: a send queues bytes at the target until <see cref="Deliver"/>.</summary>
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

        private const ushort Ask = 10, Answer = 11;

        /// <summary>Every worker answers "your number times my index".</summary>
        private static WorkerQuery Multiplier(FakeMesh mesh, FakeWorker w)
        {
            return new WorkerQuery(w, Ask, Answer, (fromId, from, req, reply) => reply.WriteInt(req.ReadInt() * w.WorkerIndex), () => mesh.Now);
        }

        [Test]
        public void RepliesFromEveryTargetCompleteTheRequest()
        {
            var mesh = new FakeMesh();
            var w1 = mesh.Add(1); var w2 = mesh.Add(2); var w3 = mesh.Add(3);
            var q1 = Multiplier(mesh, w1); Multiplier(mesh, w2); Multiplier(mesh, w3);
            var got = new Dictionary<ushort, int>();
            bool? complete = null;
            uint id = q1.Send(new ushort[] { 2, 3, 3 }, w => w.WriteInt(7), (fromId, from, r) => got[from] = r.ReadInt(), ok => complete = ok, 1f);
            Assert.AreNotEqual(0u, id);
            Assert.AreEqual(1, q1.PendingCount);
            Assert.IsNull(complete);
            mesh.Deliver();
            Assert.AreEqual(true, complete);
            Assert.AreEqual(0, q1.PendingCount);
            Assert.AreEqual(2, got.Count);
            Assert.AreEqual(14, got[2]);
            Assert.AreEqual(21, got[3]);
            Assert.AreEqual(1, q1.RequestsSent);
            Assert.AreEqual(2, q1.RepliesReceived);
            Assert.AreEqual(0, q1.RepliesTimedOut);
        }

        [Test]
        public void TimeoutCompletesWithFalseAndLateRepliesAreDropped()
        {
            var mesh = new FakeMesh();
            var w1 = mesh.Add(1); var w2 = mesh.Add(2); var w3 = mesh.Add(3);
            var q1 = Multiplier(mesh, w1); Multiplier(mesh, w2); Multiplier(mesh, w3);
            int replies = 0;
            bool? complete = null;
            q1.Send(new ushort[] { 2, 3 }, w => w.WriteInt(1), (fromId, from, r) => replies++, ok => complete = ok, 0.3f);
            // Only w2 answers in time.
            w3.Inbox.Clear();
            mesh.Deliver();
            Assert.AreEqual(1, replies);
            Assert.IsNull(complete);
            mesh.Now = 0.2f;
            q1.Update();
            Assert.IsNull(complete);
            mesh.Now = 0.3f;
            q1.Update();
            Assert.AreEqual(false, complete);
            Assert.AreEqual(1, q1.RepliesTimedOut);
            Assert.AreEqual(0, q1.PendingCount);
            // w3's answer arrives after the deadline: ignored, no second completion.
            complete = null;
            w1.Inbox.Enqueue(new FakeWorker.Message { From = 3, Kind = Answer, Bytes = Answer1() });
            mesh.Deliver();
            Assert.AreEqual(1, replies);
            Assert.IsNull(complete);
            Assert.AreEqual(1, q1.RepliesLate);
        }

        private static byte[] Answer1()
        {
            var w = new NetworkWriter();
            w.WriteUInt(1);
            w.WriteInt(3);
            return w.ToArray();
        }

        [Test]
        public void UnreachableTargetsAreSkippedAndAnEmptyFanOutCompletesAtOnce()
        {
            var mesh = new FakeMesh();
            var w1 = mesh.Add(1); var w2 = mesh.Add(2);
            var q1 = Multiplier(mesh, w1); Multiplier(mesh, w2);
            w2.Connected = false;
            bool? complete = null;
            uint id = q1.Send(new ushort[] { 2, 9 }, w => w.WriteInt(1), null, ok => complete = ok, 1f);
            Assert.AreEqual(0u, id);
            Assert.AreEqual(true, complete);
            Assert.AreEqual(0, q1.PendingCount);
            Assert.AreEqual(0, q1.RequestsSent);
        }

        [Test]
        public void AskingOneselfAnswersSynchronouslyAndStillCompletesOnce()
        {
            var mesh = new FakeMesh();
            var w1 = mesh.Add(1); var w2 = mesh.Add(2);
            var q1 = Multiplier(mesh, w1); Multiplier(mesh, w2);
            int completions = 0;
            var got = new Dictionary<ushort, int>();
            q1.Send(new ushort[] { 1, 2 }, w => w.WriteInt(5), (fromId, from, r) => got[from] = r.ReadInt(), ok => completions++, 1f);
            Assert.AreEqual(1, got.Count, "our own answer is in before Send returns");
            Assert.AreEqual(5, got[1]);
            Assert.AreEqual(0, completions, "w2 is still owed");
            mesh.Deliver();
            Assert.AreEqual(10, got[2]);
            Assert.AreEqual(1, completions);
            Assert.AreEqual(0, q1.PendingCount);

            // Only ourselves: complete inside Send, exactly once.
            completions = 0;
            uint id = q1.Send(new ushort[] { 1 }, w => w.WriteInt(2), null, ok => { completions++; Assert.IsTrue(ok); }, 1f);
            Assert.AreEqual(1, completions);
            Assert.AreEqual(0, q1.PendingCount);
        }

        [Test]
        public void CancelAndDisposeSilenceCallbacks()
        {
            var mesh = new FakeMesh();
            var w1 = mesh.Add(1); var w2 = mesh.Add(2);
            var q1 = Multiplier(mesh, w1); var q2 = Multiplier(mesh, w2);
            int calls = 0;
            uint id = q1.Send(new ushort[] { 2 }, w => w.WriteInt(1), (a, b, c) => calls++, ok => calls++, 1f);
            Assert.IsTrue(q1.Cancel(id));
            Assert.IsFalse(q1.Cancel(id));
            mesh.Deliver();
            Assert.AreEqual(0, calls);
            Assert.AreEqual(1, q1.RepliesLate);

            q1.Send(new ushort[] { 2 }, w => w.WriteInt(1), (a, b, c) => calls++, ok => calls++, 1f);
            q1.Dispose();
            Assert.IsFalse(w1.Handlers.ContainsKey(Ask));
            Assert.IsFalse(w1.Handlers.ContainsKey(Answer));
            mesh.Deliver();
            Assert.AreEqual(0, calls);
            q2.Dispose();
        }

        [Test]
        public void ConcurrentRequestsAreKeptApartById()
        {
            var mesh = new FakeMesh();
            var w1 = mesh.Add(1); var w2 = mesh.Add(2);
            var q1 = Multiplier(mesh, w1); Multiplier(mesh, w2);
            var results = new List<int>();
            uint a = q1.Send(new ushort[] { 2 }, w => w.WriteInt(1), (x, y, r) => results.Add(r.ReadInt()), null, 1f);
            uint b = q1.Send(new ushort[] { 2 }, w => w.WriteInt(2), (x, y, r) => results.Add(r.ReadInt()), null, 1f);
            Assert.AreNotEqual(a, b);
            Assert.AreEqual(2, q1.PendingCount);
            mesh.Deliver();
            CollectionAssert.AreEqual(new[] { 2, 4 }, results);
            Assert.AreEqual(0, q1.PendingCount);
        }
    }
}
