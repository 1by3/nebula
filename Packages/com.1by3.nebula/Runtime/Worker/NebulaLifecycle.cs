using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nebula
{
    /// <summary>
    /// The moments at which the mesh brings a container or a scope to life, or puts it to sleep, as game hooks.
    /// Design of record: <c>docs/lifecycle-hooks.md</c>; the sequence they sit in is <c>docs/scope-lifecycle.md</c>.
    /// <para>
    /// Nebula owns the moments, not what a game does with them. Only the mesh knows when a lease lands, when a
    /// restore finished and when a retire is about to checkpoint; a game that keeps its own summary of a sleeping
    /// region — an offline population, a settlement row, a world delta — needs exactly those four instants and the
    /// order between them. Nebula never sees the summary: persistence saves entities, not worlds.
    /// </para>
    /// <list type="number">
    /// <item><see cref="OnScopeActivating"/> — a scope is coming to life on this worker, and whether the store holds
    /// any record of it (<c>hasRecords</c>: summary or records).</item>
    /// <item><see cref="OnContainerRestored"/> — a container's records have been read and restored; it is safe to
    /// spawn into it without making duplicates.</item>
    /// <item><see cref="OnBeforeRetire"/> — a part of a retiring scope, before anything is saved: the window in
    /// which a game may collapse it into its own state.</item>
    /// <item><see cref="OnRetired"/> — the part is gone: checkpointed, emptied and its lease released.</item>
    /// </list>
    /// <para>
    /// Every hook is static, because a game registers them once at boot, before a <see cref="NebulaWorker"/> exists,
    /// and there is one worker per process — the same reason <see cref="ScopeLifecycle.ShouldRetire"/> and
    /// <see cref="PhysicsIslands.IsCohesive"/> are static. All of them are raised on the main thread. A handler that
    /// throws is logged and ignored: a game's bug must never leave a scope half-retired or a restore unreported.
    /// </para>
    /// </summary>
    public static class NebulaLifecycle
    {
        /// <summary>
        /// A scope this worker holds a part of is coming to life, before any of its containers has been restored
        /// here. <c>hasRecords</c> is whether the store holds any entity record under the scope's key
        /// (<see cref="IPersistenceStore.CountRecords"/>): false on a first activation, and the signal a game uses to
        /// choose between rebuilding from its own summary and letting the records come back. Raised once per scope
        /// per activation on this worker, and again after the scope has retired and been activated again.
        /// <para>
        /// The restore of the scope's parts is held until this has been raised, so a handler may decide what to spawn
        /// before <see cref="OnContainerRestored"/> tells it what came back (<c>docs/lifecycle-hooks.md</c> D4).
        /// </para>
        /// </summary>
        public static event Action<ScopeInfo, bool> OnScopeActivating;

        /// <summary>
        /// Every persisted record of a container this worker has just gained has been read and dealt with, and this
        /// many entities came back (0 when there was nothing saved). Raised once per lease, for every container —
        /// a scope's part and an ordinary public-world container alike — and always <i>after</i> the restore, which
        /// is what makes spawning from it safe: whatever was saved is already here.
        /// <para>The container is the live <see cref="Container"/>, or null when the lease left again before the
        /// records came back.</para>
        /// </summary>
        public static event Action<Container, int> OnContainerRestored;

        /// <summary>
        /// A part of a scope the orchestrator is retiring, <b>before anything is saved and before anything is
        /// despawned</b>: the window in which a game may write its own summary of what is in the box, hand out
        /// rewards or close a session. Every handler is started at once and the retire waits for all of them, for at
        /// most <see cref="WorkerScopeLifecycle.BeforeRetireTimeoutSeconds"/>; a handler that overruns is cancelled
        /// through the token, and one that throws is logged. <b>In both cases the retire proceeds</b> — a scope must
        /// never be left half-retired because a game's task hung.
        /// </summary>
        public static event Func<Container, CancellationToken, Task> OnBeforeRetire;

        /// <summary>
        /// The part has been checkpointed, emptied and let go: its lease is released and the scope holds it no more.
        /// Raised after the mesh has finished with the container, so nothing a handler does can disturb the retire.
        /// </summary>
        public static event Action<Container> OnRetired;

        /// <summary>Whether anything is listening for <see cref="OnScopeActivating"/>; the restore gate is free when nothing is.</summary>
        internal static bool HasScopeActivating => OnScopeActivating != null;

        internal static void RaiseScopeActivating(ScopeInfo scope, bool hasRecords)
        {
            var handlers = OnScopeActivating;
            if (handlers == null || scope == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<ScopeInfo, bool>)d)(scope, hasRecords); }
                catch (Exception e) { NebulaLog.Error($"an OnScopeActivating handler threw for scope '{scope.ScopeKey}': {e}"); }
            }
        }

        internal static void RaiseContainerRestored(Container container, int restored)
        {
            var handlers = OnContainerRestored;
            if (handlers == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<Container, int>)d)(container, restored); }
                catch (Exception e) { NebulaLog.Error($"an OnContainerRestored handler threw: {e}"); }
            }
        }

        /// <summary>
        /// Start every <see cref="OnBeforeRetire"/> handler and return the task the retire waits on, or null when
        /// nothing is listening (the retire then carries straight on). A handler that throws synchronously, or
        /// returns a faulted task, is logged and does not fault the others: the result completes when all of them
        /// have, however they ended.
        /// </summary>
        internal static Task RaiseBeforeRetire(Container container, CancellationToken cancel)
        {
            var handlers = OnBeforeRetire;
            if (handlers == null) return null;
            var list = handlers.GetInvocationList();
            var tasks = new Task[list.Length];
            for (int i = 0; i < list.Length; i++)
            {
                Task task;
                try { task = ((Func<Container, CancellationToken, Task>)list[i])(container, cancel) ?? Task.CompletedTask; }
                catch (Exception e)
                {
                    NebulaLog.Error($"an OnBeforeRetire handler threw for {Id(container)}: {e}");
                    task = Task.CompletedTask;
                }
                // Observe every outcome here: one handler's failure is logged and the retire still waits for the rest.
                tasks[i] = task.ContinueWith(t =>
                {
                    if (t.IsFaulted) NebulaLog.Error($"an OnBeforeRetire handler failed for {Id(container)}: {t.Exception?.GetBaseException().Message}");
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            return Task.WhenAll(tasks);
        }

        internal static void RaiseRetired(Container container)
        {
            var handlers = OnRetired;
            if (handlers == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((Action<Container>)d)(container); }
                catch (Exception e) { NebulaLog.Error($"an OnRetired handler threw for {Id(container)}: {e}"); }
            }
        }

        private static string Id(Container container) => container != null ? container.ContainerId : "(gone)";

        /// <summary>
        /// Forget every handler. Called when a play session starts (<c>NebulaStatics</c>), because with "enter play
        /// mode without domain reload" a static event would otherwise still hold the previous session's subscribers,
        /// and by tests that register one.
        /// </summary>
        public static void Reset()
        {
            OnScopeActivating = null;
            OnContainerRestored = null;
            OnBeforeRetire = null;
            OnRetired = null;
        }
    }
}
