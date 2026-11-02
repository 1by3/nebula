using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine.TestTools;
using System.Text.RegularExpressions;

namespace Nebula.Tests
{
    /// <summary>NEB-501: simulation batches are flushed in registration order, never defer what a flush runs, and one that throws does not stop the rest.</summary>
    public sealed class SimulationBatchesTests
    {
        private sealed class Recorder : ISimulationBatch
        {
            public readonly List<string> Log;
            public readonly string Name;
            public bool Throw;
            public Recorder(List<string> log, string name) { Log = log; Name = name; }
            public void Flush(uint tick)
            {
                Log.Add($"{Name}@{tick} deferring={SimulationBatches.Deferring}");
                if (Throw) throw new System.InvalidOperationException("boom");
            }
        }

        private readonly List<Recorder> _registered = new List<Recorder>();

        [TearDown]
        public void TearDown()
        {
            foreach (var r in _registered) SimulationBatches.Unregister(r);
            _registered.Clear();
            SimulationBatches.Reset();
        }

        private Recorder Make(List<string> log, string name)
        {
            var r = new Recorder(log, name);
            _registered.Add(r);
            SimulationBatches.Register(r);
            return r;
        }

        [Test]
        public void BatchesFlushInOrderOutsideTheDeferralAndPastOneThatThrows()
        {
            var log = new List<string>();
            var a = Make(log, "a");
            Make(log, "b");
            SimulationBatches.Register(a); // twice is once
            a.Throw = true;
            SimulationBatches.Deferring = true;
            LogAssert.Expect(UnityEngine.LogType.Error, new Regex("simulation batch Recorder threw"));
            SimulationBatches.FlushAll(7);
            Assert.AreEqual(new[] { "a@7 deferring=False", "b@7 deferring=False" }, log.ToArray());
            Assert.IsTrue(SimulationBatches.Deferring, "the pass defers again after the flush");
        }
    }
}
