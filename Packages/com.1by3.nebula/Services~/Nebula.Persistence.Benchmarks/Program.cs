using System.Diagnostics;
using Nebula;
using Nebula.ServicePrimitives;

const double CheckpointCadenceSeconds = 5.0;
int[] counts = args.Length == 0 ? new[] { 1_000, 10_000, 100_000 } : args.Select(ParseCount).ToArray();
int[] workers = { 4, 16, 64 };
string root = Path.Combine(Path.GetTempPath(), "nebula-persistence-benchmark-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

Console.WriteLine("records,fileMiB,initialWriteMs,workers,rewritesPerCheckpoint,rewritesPerSecond,meanRewriteMs,ackP50Ms,ackP99Ms,tickP50Ms,tickP99Ms,tickMaxMs");
try
{
    foreach (int count in counts)
    {
        string file = Path.Combine(root, $"entities-{count}.bin");
        using var store = new LocalPersistenceStore(file);
        store.Connect();
        for (int i = 0; i < count; i++) store.Save(Record(i, StateSize(i)));
        long beforeInitial = store.FileWriteCount;
        double beforeInitialMs = store.TotalFileWriteMilliseconds;
        store.Flush();
        double initialWriteMs = store.TotalFileWriteMilliseconds - beforeInitialMs;
        if (store.FileWriteCount == beforeInitial) throw new InvalidOperationException("initial write did not complete");
        double fileMiB = new FileInfo(file).Length / 1048576.0;
        var host = new PersistenceHost(store, null);

        foreach (int workerCount in workers)
        {
            long writesBefore = store.FileWriteCount;
            double writeMsBefore = store.TotalFileWriteMilliseconds;
            var pending = new List<(long Started, OrchestratorHttpServer.Request Request)>(workerCount);
            var tickTimes = new List<double>();
            var clock = Stopwatch.StartNew();

            long tickStart = clock.ElapsedTicks;
            for (int worker = 0; worker < workerCount; worker++)
            {
                var batch = new PersistedEntityRecord[Math.Min(16, count)];
                for (int i = 0; i < batch.Length; i++)
                {
                    int key = (worker * batch.Length + i) % count;
                    batch[i] = Record(key, StateSize(key), (uint)(100 + workerCount));
                }
                var request = new OrchestratorHttpServer.Request
                {
                    Method = "POST",
                    Path = PersistenceHost.Prefix + "/save",
                    Body = PersistedRecordJson.WriteList(batch)
                };
                long started = clock.ElapsedTicks;
                if (!host.TryHandle(request, out var response) || !response.IsPending) throw new InvalidOperationException("host did not defer save");
                pending.Add((started, request));
            }
            store.Tick();
            tickTimes.Add(ElapsedMs(tickStart, clock.ElapsedTicks));

            while (pending.Any(p => !p.Request.Completion.Task.IsCompleted))
            {
                Thread.Sleep(16); // the orchestrator normally pumps at the 60 Hz simulation rate
                tickStart = clock.ElapsedTicks;
                store.Tick();
                tickTimes.Add(ElapsedMs(tickStart, clock.ElapsedTicks));
                if (clock.Elapsed.TotalSeconds > 5) throw new TimeoutException("barriers did not complete within 5 seconds");
            }

            double[] acknowledgements = pending.Select(p => ElapsedMs(p.Started, clock.ElapsedTicks)).OrderBy(x => x).ToArray();
            tickTimes.Sort();
            long rewrites = store.FileWriteCount - writesBefore;
            double writeMs = store.TotalFileWriteMilliseconds - writeMsBefore;
            Console.WriteLine(string.Join(',',
                count,
                fileMiB.ToString("0.00"),
                initialWriteMs.ToString("0.00"),
                workerCount,
                rewrites,
                (rewrites / CheckpointCadenceSeconds).ToString("0.00"),
                (rewrites == 0 ? 0 : writeMs / rewrites).ToString("0.00"),
                Percentile(acknowledgements, 0.50).ToString("0.00"),
                Percentile(acknowledgements, 0.99).ToString("0.00"),
                Percentile(tickTimes, 0.50).ToString("0.00"),
                Percentile(tickTimes, 0.99).ToString("0.00"),
                tickTimes[^1].ToString("0.00")));
        }
    }
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

static int ParseCount(string text) => int.Parse(text.Replace("k", "000", StringComparison.OrdinalIgnoreCase));
static int StateSize(int i) => i % 3 switch { 0 => 128, 1 => 512, _ => 1984 };
static double ElapsedMs(long start, long end) => (end - start) * 1000.0 / Stopwatch.Frequency;
static double Percentile(IReadOnlyList<double> sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * p) - 1, 0, sorted.Count - 1)];

static PersistedEntityRecord Record(int i, int stateSize, uint epoch = 1)
{
    var state = new byte[stateSize];
    for (int n = 0; n < state.Length; n++) state[n] = (byte)(i * 31 + n);
    return new PersistedEntityRecord
    {
        Key = "entity-" + i.ToString("D8"),
        PrefabId = 3,
        PrefabName = "PersistentCrate",
        SceneId = 0,
        ContainerId = "cell-" + i % 256,
        CarrierKey = "",
        LocalPosition = new Vector3(i % 100, 1.5f, i % 73),
        LocalRotation = new Quaternion(0, 0.7071068f, 0, 0.7071068f),
        Velocity = new Vector3(0, 0, 0),
        Epoch = epoch,
        ServerDriven = true,
        Owned = false,
        Name = "crate " + i,
        State = state,
        SavedBy = "benchmark-worker"
    };
}
