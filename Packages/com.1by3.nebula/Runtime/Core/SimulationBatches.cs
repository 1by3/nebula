using System;
using System.Collections.Generic;

namespace Nebula
{
    /// <summary>
    /// Work a game collects from many entities during the worker's simulate pass and runs together, so it can be spread over
    /// the worker's cores (NEB-501): physics queries batched with <c>RaycastCommand</c>, <c>SpherecastCommand</c> and their
    /// kin, or pure maths in a job, with the results applied on the main thread afterwards.
    /// </summary>
    /// <remarks>
    /// While <see cref="Deferring"/> is true (only inside the worker's simulate pass), an entity's <c>NetworkTick</c> may
    /// enqueue part of its step in an <see cref="ISimulationBatch"/> instead of running it. The worker flushes every
    /// registered batch at the end of each nesting depth of that pass, before the physics scene is brought up to date for
    /// the depth below (so a pawn's deferred move still happens before what rides on it, and after its carrier), and in
    /// registration order. A batch's <see cref="ISimulationBatch.Flush"/> finishes every step it holds, in the order they
    /// were enqueued, so an entity's step ends where it would have serially as long as nothing it reads was changed by
    /// another entity of the same depth in between (Nebula does not sync transforms between entities of one depth, so
    /// queries never saw those changes anyway). A client, and every caller outside the pass, sees <see cref="Deferring"/>
    /// false and runs its step at once: prediction and replays are unchanged. An entity stepped twice in one
    /// <c>NetworkTick</c> (a skipped tick replayed, <see cref="PredictedBehaviourBase.ReplaySkippedTicks"/>) must finish its
    /// pending step before it starts the next.
    /// </remarks>
    public interface ISimulationBatch
    {
        /// <summary>Finish every step enqueued since the last flush, at simulation tick <paramref name="tick"/>.</summary>
        void Flush(uint tick);
    }

    /// <inheritdoc cref="ISimulationBatch"/>
    public static class SimulationBatches
    {
        private static readonly List<ISimulationBatch> Batches = new List<ISimulationBatch>();

        /// <summary>True only inside the worker's simulate pass: an entity's step may be enqueued in a batch that is flushed at the end of its depth.</summary>
        public static bool Deferring { get; internal set; }

        /// <summary>Flush <paramref name="batch"/> with the others at the end of each depth of the worker's simulate pass. Registering twice is a no-op.</summary>
        public static void Register(ISimulationBatch batch)
        {
            if (batch != null && !Batches.Contains(batch)) Batches.Add(batch);
        }

        public static void Unregister(ISimulationBatch batch) => Batches.Remove(batch);

        /// <summary>Flush every registered batch (the worker, at the end of each depth). A batch that throws is logged and the rest still run.</summary>
        internal static void FlushAll(uint tick)
        {
            bool was = Deferring;
            Deferring = false; // what a flush runs is not deferred again
            try
            {
                for (int i = 0; i < Batches.Count; i++)
                {
                    try { Batches[i].Flush(tick); }
                    catch (Exception e) { NebulaLog.Error($"simulation batch {Batches[i].GetType().Name} threw: {e}"); }
                }
            }
            finally { Deferring = was; }
        }

        internal static void Reset() => Deferring = false;
    }
}
