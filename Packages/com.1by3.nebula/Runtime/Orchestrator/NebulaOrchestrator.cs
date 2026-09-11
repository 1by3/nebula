using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Nebula.Hosting;
using UnityEngine;

namespace Nebula
{
    /// <summary>One line of the orchestrator's recent history, shown on the dashboard.</summary>
    public struct OrchestratorEvent
    {
        public DateTime Time;
        public string Level;
        public string Message;
    }

    /// <summary>
    /// Spins worker processes up and down and authoritatively assigns containers to them through the control plane.
    /// The number of workers is a live setting: <see cref="SetDesiredWorkers"/> (from the web dashboard, see
    /// <see cref="OrchestratorHttpServer"/>) launches new processes or retires existing ones on the fly.
    /// <para>
    /// Assignment policy (v1): containers are dealt as evenly as possible across live workers, sticky to their current
    /// owner so a rebalance moves as few containers as possible. Four workers and four containers means one each;
    /// three workers means one of them simulates two, and so on.
    /// </para><para>
    /// Removing a worker is graceful: it is excluded from the assignment set, so the next pass moves its containers
    /// to the survivors and the worker hands its entities over through the normal per-entity handover path. Once it
    /// holds no leases and reports no authoritative entities (or the drain timeout passes) the process is killed.
    /// A worker whose heartbeat stops is declared dead, its containers are reassigned immediately, and a replacement
    /// is launched after a short delay.
    /// </para>
    /// </summary>
    public sealed class NebulaOrchestrator : MonoBehaviour
    {
        private sealed class ManagedWorker
        {
            public string Id;
            public uint Index;
            /// <summary>The host's view of the instance; null when a launch failed outright.</summary>
            public IWorkerHandle Handle;
            public float LaunchedAt;
            public float RelaunchAt = -1f;
            public bool Retiring;
            public float RetireDeadline;
        }

        public const int MaxWorkers = 32;
        private const int MaxEvents = 200;

        public NebulaConfig Config { get; private set; }
        public IControlPlane ControlPlane { get; private set; }
        public string OrchestratorId { get; private set; } = "orch1";
        public int DesiredWorkers { get; private set; }
        public int Rebalances { get; private set; }
        public IReadOnlyList<string> LastAssignmentLog => _log;
        public IReadOnlyList<OrchestratorEvent> Events => _events;
        public string DashboardUrl => _http != null ? _http.Url : "";
        /// <summary>The World map's data: static container geometry and the latest telemetry each worker posted (see <see cref="WorkerTelemetry"/>).</summary>
        public MeshTelemetry Telemetry { get; } = new MeshTelemetry();

        private readonly List<ManagedWorker> _managed = new List<ManagedWorker>();
        private readonly List<string> _log = new List<string>();
        private readonly List<OrchestratorEvent> _events = new List<OrchestratorEvent>();
        /// <summary>Workers we have seen alive during this orchestrator's lifetime. Rows left behind by a previous run are never "declared dead", only reset.</summary>
        private readonly HashSet<string> _seenAlive = new HashSet<string>();
        /// <summary>Workers being drained: excluded from assignment, killed once empty. Keyed by id so unmanaged (externally started) workers can be retired too.</summary>
        private readonly Dictionary<string, float> _retiring = new Dictionary<string, float>();
        /// <summary>Recently retired ids -> time; their control-plane row may linger for a pass or two and must not be "declared dead".</summary>
        private readonly Dictionary<string, float> _retired = new Dictionary<string, float>();
        private readonly StringBuilder _json = new StringBuilder(8192);
        private Process _gatewayProcess;
        private OrchestratorHttpServer _http;
        /// <summary>Where workers run (child processes or cloud machines).</summary>
        private IWorkerHost _host;
        /// <summary>Always local: starts the gateway next to the orchestrator.</summary>
        private ProcessWorkerHost _local;
        private float _nextPass;
        private float _nextPublish;
        private bool _containersEnsured;
        private bool _gatewayLaunched;
        private bool _hostErrorLogged;

        public string HostName => _host != null ? _host.Name : "";

        public void Initialize(NebulaConfig config, IControlPlane controlPlane)
        {
            Config = config;
            ControlPlane = controlPlane;
            DesiredWorkers = Mathf.Clamp(config.WorkerCount, 0, MaxWorkers);
            OrchestratorId = CommandLine.Get("nebula-orchestrator-id", "orch1");
            _local = new ProcessWorkerHost(config.WorkerExecutable, config.WorkerAdvertiseAddress);
            _host = CreateHost(config);
            Log("info", $"orchestrator {OrchestratorId}: desired workers = {DesiredWorkers}, host={_host.Name}, spawnGateway={config.OrchestratorSpawnsGateway}");
            Telemetry.PublishGeometry(MeshTelemetry.BuildGeometryJson(config));
            StartDashboard();
            _host.Initialize(Log);
            if (!ReferenceEquals(_host, _local)) _local.Initialize(Log);
        }

        /// <summary>Pick the worker host from <see cref="NebulaConfig.WorkerHost"/> (<c>-nebula-host</c>). New providers register here.</summary>
        private IWorkerHost CreateHost(NebulaConfig config)
        {
            switch ((config.WorkerHost ?? "process").Trim().ToLowerInvariant())
            {
                case "hetzner":
                    return new HetznerWorkerHost(CloudHostSettings.FromCommandLine(OrchestratorId, config.WorkerAdvertiseAddress, config.DashboardPort));
                case "":
                case "process":
                case "local":
                    return _local;
                default:
                    Log("error", $"unknown worker host '{config.WorkerHost}'; using local processes");
                    return _local;
            }
        }

        private void StartDashboard()
        {
            if (Config.DashboardPort == 0) return;
            try
            {
                var page = Resources.Load<TextAsset>("NebulaDashboard");
                string bind = CommandLine.Get("nebula-dashboard-bind", "localhost");
                string artifacts = Config.BuildArtifactDir;
                if (string.IsNullOrEmpty(artifacts))
                {
                    try { artifacts = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName); } catch { artifacts = null; }
                }
                _http = new OrchestratorHttpServer(bind, Config.DashboardPort, page != null ? page.text : null, artifacts);
                var map = Resources.Load<TextAsset>("NebulaDashboardMap");
                if (map != null) _http.AddPage("/map", map.text);
                // Telemetry and the map document are served on the listener thread: workers post several times a
                // second, and nothing here touches Unity.
                _http.MapDirect("POST", "/api/telemetry", req =>
                {
                    string error = Telemetry.Accept(req.Body, out bool detail);
                    return error != null
                        ? OrchestratorHttpServer.Response.Error(400, error)
                        : OrchestratorHttpServer.Response.Json(200, detail ? "{\"ok\":true,\"detail\":true}" : "{\"ok\":true,\"detail\":false}");
                });
                _http.MapDirect("GET", "/api/map", _ => OrchestratorHttpServer.Response.Json(200, Telemetry.BuildMapJson()));
                _http.MapDirect("GET", "/api/map/geometry", _ => OrchestratorHttpServer.Response.Json(200, Telemetry.GeometryJson));
                _http.Start();
                Log("info", $"dashboard at {_http.Url} (bind {bind}); World map at {_http.Url}map; workers post telemetry to {TelemetryUrl()}; artifacts from {artifacts ?? "(none)"}");
            }
            catch (Exception e)
            {
                Log("error", $"dashboard failed to start on port {Config.DashboardPort}: {e.Message}");
                _http = null;
            }
        }

        private void Update()
        {
            // Dashboard commands run here so every mutation happens on the main thread; the response waits for us.
            _http?.Pump(HandleCommand);
            _host.Tick();
            if (!ReferenceEquals(_host, _local)) _local.Tick();

            if (!ControlPlane.IsConnected)
            {
                if (Time.unscaledTime >= _nextPublish) PublishState();
                return;
            }
            if (!_host.IsReady)
            {
                if (!string.IsNullOrEmpty(_host.InitializationError) && !_hostErrorLogged)
                {
                    _hostErrorLogged = true;
                    Log("error", $"worker host '{_host.Name}' unavailable: {_host.InitializationError}. No workers will be launched.");
                }
                if (Time.unscaledTime >= _nextPublish) PublishState();
                return;
            }
            if (!_containersEnsured)
            {
                _containersEnsured = true;
                if (CommandLine.GetBool("nebula-reset", true)) ControlPlane.ResetControlPlane();
                foreach (var c in ContainerRegistry.All) ControlPlane.EnsureContainer(c.ContainerId);
                SeedSettings();
                if (!Config.UseLocalControlPlane) LaunchGateway();
                _nextPass = Time.unscaledTime + 1f; // let the reset land before judging anybody
                return;
            }
            if (Time.unscaledTime < _nextPass) return;
            _nextPass = Time.unscaledTime + 0.5f;

            ControlPlane.HeartbeatOrchestrator(OrchestratorId, (uint)DesiredWorkers);
            ReapDeadWorkers();
            ReconcileDesiredCount();
            Rebalance();
            FinishRetirements();
            RelaunchIfNeeded();
            PublishState();
        }

        private void OnDestroy()
        {
            _http?.Dispose();
            foreach (var m in _managed) if (m.Handle != null) _host.Kill(m.Handle);
            _host?.Dispose();
            if (!ReferenceEquals(_host, _local)) _local?.Dispose();
            ProcessWorkerHost.KillProcess(_gatewayProcess);
        }

        // ---------------------------------------------------------------------------------------- public controls

        /// <summary>Set how many workers should be running. Extra workers are launched; surplus ones are drained and killed (highest index first).</summary>
        public void SetDesiredWorkers(int count)
        {
            count = Mathf.Clamp(count, 0, MaxWorkers);
            if (count == DesiredWorkers) return;
            Log("info", $"desired workers {DesiredWorkers} -> {count}");
            DesiredWorkers = count;
            _nextPass = 0f; // act on the next frame
        }

        public void AddWorker() => SetDesiredWorkers(DesiredWorkers + 1);

        /// <summary>
        /// Seed the mesh-wide settings from <c>-nebula-settings key=value,key=value</c>. Nebula attaches no meaning
        /// to them; the game reads them on every worker (ShooterGame keeps its NPC total in <c>npcs</c>).
        /// </summary>
        private void SeedSettings()
        {
            string spec = CommandLine.Get("nebula-settings", "");
            if (string.IsNullOrWhiteSpace(spec)) return;
            foreach (var part in spec.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) { Log("warn", $"-nebula-settings: ignoring '{part}' (expected key=value)"); continue; }
                string key = part.Substring(0, eq).Trim(), value = part.Substring(eq + 1).Trim();
                Log("info", $"setting {key} = {value}");
                ControlPlane.SetSetting(key, value);
            }
        }

        /// <summary>Set one mesh-wide setting (dashboard). Game code on the workers decides what it means.</summary>
        public bool SetSetting(string key, string value)
        {
            key = (key ?? "").Trim();
            if (key.Length == 0 || key.Length > 64 || (value ?? "").Length > 1024) return false;
            Log("info", $"setting {key} = {value}");
            ControlPlane.SetSetting(key, value ?? "");
            return true;
        }

        /// <summary>Gracefully remove one worker: a specific one, or the highest-index one when <paramref name="workerId"/> is empty.</summary>
        public bool RemoveWorker(string workerId = null)
        {
            if (string.IsNullOrEmpty(workerId))
            {
                if (DesiredWorkers == 0) return false;
                SetDesiredWorkers(DesiredWorkers - 1);
                return true;
            }
            if (_retiring.ContainsKey(workerId)) return false;
            bool known = _managed.Any(m => m.Id == workerId && !m.Retiring) || ControlPlane.FindWorker(workerId) != null;
            if (!known) return false;
            BeginRetire(workerId, "removed from dashboard");
            // A managed worker counted toward the desired total; lower it so the reconciler does not replace it at once.
            if (_managed.Any(x => x.Id == workerId)) DesiredWorkers = Mathf.Max(0, DesiredWorkers - 1);
            _nextPass = 0f;
            return true;
        }

        /// <summary>Hard-kill a managed worker process (simulates a crash). The reaper reassigns its containers and relaunches it.</summary>
        public bool KillWorker(string workerId)
        {
            var m = _managed.FirstOrDefault(x => x.Id == workerId);
            if (m == null || m.Handle == null) return false;
            Log("warn", $"killing worker {workerId} ({m.Handle.Describe}) to simulate a crash");
            _host.Kill(m.Handle);
            _nextPass = 0f;
            return true;
        }

        /// <summary>Run an assignment pass now instead of waiting for the next 500 ms tick.</summary>
        public void RequestRebalance()
        {
            Log("info", "rebalance requested");
            _nextPass = 0f;
        }

        // ---------------------------------------------------------------------------------------- processes

        private void LaunchGateway()
        {
            if (Config.OrchestratorSpawnsGateway && !_gatewayLaunched)
            {
                _gatewayLaunched = true;
                // The gateway registers the address clients use (-nebula-gateway on the orchestrator, e.g. its public IP).
                _gatewayProcess = _local.LaunchService("gateway", "-nebula-gateway-id gw1", "gateway", CommonArgs());
            }
        }

        /// <summary>Switches every launched role inherits from the orchestrator.</summary>
        private string CommonArgs()
        {
            string args = $"-nebula-spacetime {Config.SpacetimeUri} -nebula-database {Config.SpacetimeDatabase} -nebula-gateway {Config.GatewayAddress}:{Config.GatewayPort} -nebula-telemetry {TelemetryUrl()}";
            if (CommandLine.GetBool("nebula-verbose", false)) args += " -nebula-verbose";
            return args;
        }

        /// <summary>
        /// Where launched workers post World map telemetry: <c>-nebula-telemetry</c> when given (<c>off</c> disables
        /// it), otherwise this orchestrator's dashboard at the address it advertises, which is how worker VMs already
        /// reach it for the build. <c>off</c> when the dashboard is disabled.
        /// </summary>
        private string TelemetryUrl()
        {
            if (Config.DashboardPort == 0) return "off";
            string url = CommandLine.Get("nebula-telemetry", "");
            if (!string.IsNullOrEmpty(url)) return url;
            string host = string.IsNullOrEmpty(Config.WorkerAdvertiseAddress) ? "127.0.0.1" : Config.WorkerAdvertiseAddress;
            return $"http://{host}:{Config.DashboardPort}/api/telemetry";
        }

        private void LaunchWorker(uint index)
        {
            string id = $"w{index}";
            var existing = _managed.FirstOrDefault(m => m.Index == index);
            if (existing == null)
            {
                existing = new ManagedWorker { Id = id, Index = index };
                _managed.Add(existing);
            }
            existing.RelaunchAt = -1f;
            existing.LaunchedAt = Time.unscaledTime;
            var spec = new WorkerLaunchSpec
            {
                WorkerId = id,
                Index = index,
                Port = (ushort)(Config.WorkerBasePort + index),
                CommonArgs = CommonArgs(),
            };
            var handle = _host.Launch(spec);
            existing.Handle = handle.State == WorkerHandleState.Failed ? null : handle;
            if (existing.Handle == null) Log("error", $"launching {id} on host '{_host.Name}' failed: {handle.Reason}");
            else Log("info", $"launched worker {id} on {_host.Name}: {handle.Describe}");
        }

        private static bool HandleGone(ManagedWorker m)
        {
            return m.Handle == null || m.Handle.State == WorkerHandleState.Exited || m.Handle.State == WorkerHandleState.Failed;
        }

        private void ReapDeadWorkers()
        {
            float now = Time.unscaledTime;
            // Forget retirements old enough that their control-plane row is certainly gone.
            foreach (var id in _retired.Where(kv => now - kv.Value > 15f).Select(kv => kv.Key).ToList()) _retired.Remove(id);

            foreach (var w in ControlPlane.Workers.ToList())
            {
                if (w.Status == WorkerStatus.Dead || _retired.ContainsKey(w.WorkerId)) continue;
                if (ControlPlane.IsWorkerAlive(w, Config.WorkerTimeoutSeconds))
                {
                    _seenAlive.Add(w.WorkerId);
                    continue;
                }
                // A row we never saw alive is a leftover from an earlier run (the reset removes it); not a death.
                if (!_seenAlive.Contains(w.WorkerId)) continue;
                _seenAlive.Remove(w.WorkerId);
                Log("warn", $"worker {w.WorkerId} missed heartbeats for {(ControlPlane.Now - w.LastHeartbeat).TotalSeconds:F1}s; declaring dead");
                ControlPlane.UnregisterWorker(w.WorkerId);
                Telemetry.Forget(w.WorkerId);
                var m = _managed.FirstOrDefault(x => x.Id == w.WorkerId);
                if (m != null) OnManagedWorkerDead(m);
                else _retiring.Remove(w.WorkerId);
            }
            // A managed instance the host reports gone is dead even if its last heartbeat is recent.
            foreach (var m in _managed.ToList())
            {
                if (m.Handle == null && m.RelaunchAt < 0f && !m.Retiring && !Config.UseLocalControlPlane)
                {
                    // Launch failed outright (no executable, cloud API error); keep trying at the replacement cadence.
                    m.RelaunchAt = Time.unscaledTime + Config.DeadWorkerReplaceDelaySeconds;
                    continue;
                }
                if (m.Handle != null && HandleGone(m) && m.RelaunchAt < 0f)
                {
                    Log("warn", $"worker {m.Id} instance gone ({m.Handle.Reason})");
                    ControlPlane.UnregisterWorker(m.Id);
                    _seenAlive.Remove(m.Id);
                    OnManagedWorkerDead(m);
                }
            }
        }

        private void OnManagedWorkerDead(ManagedWorker m)
        {
            if (m.Handle != null) _host.Kill(m.Handle);
            m.Handle = null;
            if (m.Retiring)
            {
                // It was on its way out anyway; its leases are orphaned by the unregister and the next pass deals them out.
                _managed.Remove(m);
                _retiring.Remove(m.Id);
                _retired[m.Id] = Time.unscaledTime;
                Log("info", $"worker {m.Id} retired (process gone)");
            }
            else
            {
                m.RelaunchAt = Time.unscaledTime + Config.DeadWorkerReplaceDelaySeconds;
            }
        }

        private void RelaunchIfNeeded()
        {
            if (Config.UseLocalControlPlane) return;
            foreach (var m in _managed)
            {
                if (m.RelaunchAt >= 0f && Time.unscaledTime >= m.RelaunchAt)
                {
                    Log("info", $"relaunching worker {m.Id}");
                    LaunchWorker(m.Index);
                }
            }
        }

        // ---------------------------------------------------------------------------------------- scaling

        /// <summary>Smallest positive index not in <paramref name="used"/> (indices double as ports and entity-id high bits, so they are reused).</summary>
        public static uint NextFreeIndex(IEnumerable<uint> used)
        {
            var taken = new HashSet<uint>(used);
            uint i = 1;
            while (taken.Contains(i)) i++;
            return i;
        }

        /// <summary>Which worker leaves first when scaling down: the highest index among those not already retiring.</summary>
        public static string PickWorkerToRetire(IEnumerable<KeyValuePair<string, uint>> candidates)
        {
            string best = null;
            uint bestIndex = 0;
            foreach (var kv in candidates)
            {
                if (best == null || kv.Value > bestIndex) { best = kv.Key; bestIndex = kv.Value; }
            }
            return best;
        }

        private void ReconcileDesiredCount()
        {
            if (Config.UseLocalControlPlane) return;
            var active = _managed.Where(m => !m.Retiring).ToList();
            int guard = 0;
            while (active.Count < DesiredWorkers && guard++ < MaxWorkers)
            {
                var used = _managed.Select(m => m.Index).Concat(ControlPlane.Workers.Select(w => w.WorkerIndex));
                uint index = NextFreeIndex(used);
                Log("info", $"scaling up: launching worker w{index}");
                LaunchWorker(index);
                active = _managed.Where(m => !m.Retiring).ToList();
            }
            while (active.Count > DesiredWorkers)
            {
                string id = PickWorkerToRetire(active.Select(m => new KeyValuePair<string, uint>(m.Id, m.Index)));
                if (id == null) break;
                BeginRetire(id, "scaling down");
                active = _managed.Where(m => !m.Retiring).ToList();
            }
        }

        private void BeginRetire(string workerId, string reason)
        {
            if (_retiring.ContainsKey(workerId)) return;
            _retiring[workerId] = Time.unscaledTime + Config.WorkerDrainTimeoutSeconds;
            var m = _managed.FirstOrDefault(x => x.Id == workerId);
            if (m != null)
            {
                m.Retiring = true;
                m.RetireDeadline = _retiring[workerId];
                m.RelaunchAt = -1f;
            }
            Log("info", $"retiring worker {workerId} ({reason}); draining its containers");
        }

        private void FinishRetirements()
        {
            if (_retiring.Count == 0) return;
            float now = Time.unscaledTime;
            foreach (var id in _retiring.Keys.ToList())
            {
                bool holdsLease = ControlPlane.Leases.Any(l => l.WorkerId == id && (l.State == LeaseState.Active || l.State == LeaseState.Assigning));
                if (holdsLease) continue; // the assignment pass has not moved its containers yet
                var row = ControlPlane.FindWorker(id);
                bool drained = row == null || row.AuthoritativeCount == 0;
                bool timedOut = now >= _retiring[id];
                if (!drained && !timedOut) continue;
                Log(timedOut && !drained ? "warn" : "info",
                    timedOut && !drained
                        ? $"worker {id} still reports {row.AuthoritativeCount} authoritative entities after the drain timeout; killing it anyway"
                        : $"worker {id} drained; shutting it down");
                ControlPlane.UnregisterWorker(id);
                _seenAlive.Remove(id);
                Telemetry.Forget(id);
                var m = _managed.FirstOrDefault(x => x.Id == id);
                if (m != null)
                {
                    if (m.Handle != null) _host.Kill(m.Handle);
                    _managed.Remove(m);
                }
                _retiring.Remove(id);
                _retired[id] = now;
            }
        }

        // ---------------------------------------------------------------------------------------- assignment

        /// <summary>
        /// Deal containers across live workers as evenly as possible, keeping existing assignments where they fit.
        /// Pure function of (containers, live workers, current leases); returns the changes to apply.
        /// </summary>
        /// <param name="keepOrder">Deal <paramref name="containerIds"/> in the order given instead of sorting by id.
        /// A partitioned world lists its containers in Morton order, so each worker's quota is a contiguous run of
        /// spatially neighbouring cells - a compact region it can load as a block.</param>
        public static List<KeyValuePair<string, string>> ComputeAssignment(IList<string> containerIds, IList<WorkerInfo> liveWorkers, IList<LeaseInfo> leases, bool keepOrder = false)
        {
            var changes = new List<KeyValuePair<string, string>>();
            if (liveWorkers.Count == 0 || containerIds.Count == 0) return changes;
            var workers = liveWorkers.OrderBy(w => w.WorkerIndex).ToList();
            var containers = keepOrder ? containerIds.ToList() : containerIds.OrderBy(c => c, StringComparer.Ordinal).ToList();
            int n = containers.Count, k = workers.Count;
            int baseQuota = n / k, extra = n % k;
            var quota = new Dictionary<string, int>();
            for (int i = 0; i < k; i++) quota[workers[i].WorkerId] = baseQuota + (i < extra ? 1 : 0);

            var alive = new HashSet<string>(workers.Select(w => w.WorkerId));
            var assignment = new Dictionary<string, string>();
            foreach (var c in containers)
            {
                var lease = leases.FirstOrDefault(l => l.ContainerId == c);
                string owner = lease != null && lease.State == LeaseState.Active && alive.Contains(lease.WorkerId) ? lease.WorkerId : "";
                assignment[c] = owner;
            }
            var count = workers.ToDictionary(w => w.WorkerId, w => 0);
            var unassigned = new List<string>();
            foreach (var c in containers)
            {
                string owner = assignment[c];
                if (owner == "") { unassigned.Add(c); continue; }
                if (count[owner] >= quota[owner]) { assignment[c] = ""; unassigned.Add(c); continue; }
                count[owner]++;
            }
            foreach (var c in unassigned)
            {
                foreach (var w in workers)
                {
                    if (count[w.WorkerId] < quota[w.WorkerId])
                    {
                        assignment[c] = w.WorkerId;
                        count[w.WorkerId]++;
                        break;
                    }
                }
            }
            foreach (var c in containers)
            {
                var lease = leases.FirstOrDefault(l => l.ContainerId == c);
                string current = lease != null && lease.State == LeaseState.Active ? lease.WorkerId : "";
                if (assignment[c] != current && assignment[c] != "") changes.Add(new KeyValuePair<string, string>(c, assignment[c]));
            }
            return changes;
        }

        private List<WorkerInfo> LiveWorkers() =>
            ControlPlane.Workers.Where(w => w.Status != WorkerStatus.Dead && ControlPlane.IsWorkerAlive(w, Config.WorkerTimeoutSeconds)).ToList();

        private void Rebalance()
        {
            var live = LiveWorkers();
            foreach (var w in live) _seenAlive.Add(w.WorkerId);
            // Retiring workers stay alive for the drain but receive nothing new.
            var eligible = live.Where(w => !_retiring.ContainsKey(w.WorkerId) && !_retired.ContainsKey(w.WorkerId)).ToList();
            var ids = ContainerRegistry.All.Select(c => c.ContainerId).ToList();
            if (eligible.Count == 0 && _retiring.Count > 0)
            {
                // Scaling to zero: there is nobody to hand over to, so the leases are released and the retirees drain by timeout.
                foreach (var l in ControlPlane.Leases.ToList())
                {
                    if (_retiring.ContainsKey(l.WorkerId) && l.State != LeaseState.Orphaned)
                    {
                        Log("warn", $"no eligible worker for {l.ContainerId}; releasing its lease");
                        ControlPlane.ReleaseContainer(l.ContainerId);
                    }
                }
                return;
            }
            // Carried containers (dynamic, id 'label#netId') follow their carrier by default and are not dealt. One
            // that was pinned to a worker that is gone or retiring falls back to following its carrier.
            foreach (var l in ControlPlane.Leases.ToList())
            {
                if (!ContainerRegistry.IsDynamicId(l.ContainerId) || l.State != LeaseState.Pinned) continue;
                if (eligible.Any(w => w.WorkerId == l.WorkerId)) continue;
                Log("info", $"unpinning {l.ContainerId} from {l.WorkerId} (not eligible); it follows its carrier again");
                ControlPlane.SetLeaseState(l.ContainerId, LeaseState.Active);
            }
            var changes = ComputeAssignment(ids, eligible, ControlPlane.Leases.ToList(), keepOrder: ContainerRegistry.IsGridded);
            if (changes.Count == 0) return;
            Rebalances++;
            _log.Clear();
            foreach (var kv in changes)
            {
                ControlPlane.AssignContainer(kv.Key, kv.Value);
                var line = $"assign {kv.Key} -> {kv.Value}";
                _log.Add(line);
                Log("info", line);
            }
        }

        // ---------------------------------------------------------------------------------------- dashboard

        private void Log(string level, string message)
        {
            switch (level)
            {
                case "warn": NebulaLog.Warn(message); break;
                case "error": NebulaLog.Error(message); break;
                default: NebulaLog.Info(message); break;
            }
            _events.Add(new OrchestratorEvent { Time = DateTime.UtcNow, Level = level, Message = message });
            if (_events.Count > MaxEvents) _events.RemoveRange(0, _events.Count - MaxEvents);
        }

        private OrchestratorHttpServer.Response HandleCommand(OrchestratorHttpServer.Request req)
        {
            string path = req.Path.TrimEnd('/');
            if (req.Method == "GET" && path == "/api/state")
            {
                return OrchestratorHttpServer.Response.Json(200, BuildStateJson());
            }
            if (req.Method != "POST") return OrchestratorHttpServer.Response.Error(405, "method not allowed");
            switch (path)
            {
                case "/api/desired":
                {
                    if (!OrchestratorHttpServer.TryGetInt(req.Body, "desired", out int desired)) return OrchestratorHttpServer.Response.Error(400, "body must be {\"desired\": n}");
                    SetDesiredWorkers(desired);
                    break;
                }
                case "/api/settings":
                {
                    string key = OrchestratorHttpServer.GetString(req.Body, "key");
                    string value = OrchestratorHttpServer.GetString(req.Body, "value");
                    if (!SetSetting(key, value)) return OrchestratorHttpServer.Response.Error(400, "body must be {\"key\": \"name\", \"value\": \"text\"}");
                    break;
                }
                case "/api/workers/add":
                    if (DesiredWorkers >= MaxWorkers) return OrchestratorHttpServer.Response.Error(409, $"at most {MaxWorkers} workers");
                    AddWorker();
                    break;
                case "/api/workers/remove":
                {
                    string id = OrchestratorHttpServer.GetString(req.Body, "workerId");
                    if (!RemoveWorker(id)) return OrchestratorHttpServer.Response.Error(404, string.IsNullOrEmpty(id) ? "no workers to remove" : $"unknown or already retiring worker '{id}'");
                    break;
                }
                case "/api/workers/kill":
                {
                    string id = OrchestratorHttpServer.GetString(req.Body, "workerId");
                    if (!KillWorker(id)) return OrchestratorHttpServer.Response.Error(404, $"no managed process for worker '{id}'");
                    break;
                }
                case "/api/rebalance":
                    RequestRebalance();
                    break;
                case "/api/containers/pin":
                {
                    string id = OrchestratorHttpServer.GetString(req.Body, "containerId");
                    string workerId = OrchestratorHttpServer.GetString(req.Body, "workerId");
                    string error = PinContainer(id, workerId);
                    if (error != null) return OrchestratorHttpServer.Response.Error(400, error);
                    break;
                }
                case "/api/containers/unpin":
                {
                    string id = OrchestratorHttpServer.GetString(req.Body, "containerId");
                    string error = UnpinContainer(id);
                    if (error != null) return OrchestratorHttpServer.Response.Error(400, error);
                    break;
                }
                default:
                    return OrchestratorHttpServer.Response.Error(404, "unknown endpoint");
            }
            PublishState();
            return OrchestratorHttpServer.Response.Json(200, $"{{\"ok\":true,\"desired\":{DesiredWorkers}}}");
        }

        /// <summary>
        /// Give a carried container (a ship's interior) a worker of its own: the lease is assigned to
        /// <paramref name="workerId"/> and marked <see cref="LeaseState.Pinned"/>, so it stops following its carrier.
        /// The pinned worker receives a permanent ghost of the carrier and simulates whatever is inside. Null on
        /// success, otherwise the reason.
        /// </summary>
        public string PinContainer(string containerId, string workerId)
        {
            if (!ContainerRegistry.IsDynamicId(containerId)) return "only carried containers ('label#netId') can be pinned";
            if (ControlPlane.FindLease(containerId) == null) return $"unknown container '{containerId}'";
            var w = ControlPlane.FindWorker(workerId);
            if (w == null || !ControlPlane.IsWorkerAlive(w, Config.WorkerTimeoutSeconds) || _retiring.ContainsKey(workerId)) return $"worker '{workerId}' is not live";
            ControlPlane.AssignContainer(containerId, workerId);
            ControlPlane.SetLeaseState(containerId, LeaseState.Pinned);
            Log("info", $"pinned {containerId} -> {workerId}");
            return null;
        }

        /// <summary>Let a pinned carried container follow its carrier again. Null on success, otherwise the reason.</summary>
        public string UnpinContainer(string containerId)
        {
            var lease = ControlPlane.FindLease(containerId);
            if (lease == null) return $"unknown container '{containerId}'";
            if (lease.State != LeaseState.Pinned) return $"'{containerId}' is not pinned";
            ControlPlane.SetLeaseState(containerId, LeaseState.Active);
            Log("info", $"unpinned {containerId}; it follows its carrier again");
            return null;
        }

        private void PublishState()
        {
            _nextPublish = Time.unscaledTime + 0.5f;
            _http?.PublishState(BuildStateJson());
        }

        /// <summary>Everything the dashboard shows, as one JSON document.</summary>
        public string BuildStateJson()
        {
            var sb = _json;
            sb.Clear();
            var w = new JsonWriter(sb);
            float now = Time.unscaledTime;
            var leases = ControlPlane.Leases;
            var workers = ControlPlane.Workers;

            w.BeginObject();
            w.Prop("orchestratorId", OrchestratorId);
            w.Prop("serverTimeUtc", DateTime.UtcNow.ToString("o"));
            w.Prop("controlPlaneConnected", ControlPlane.IsConnected);
            w.Prop("localControlPlane", Config.UseLocalControlPlane);
            w.Prop("host", _host.Name);
            w.Prop("hostReady", _host.IsReady);
            w.Prop("hostError", _host.InitializationError ?? "");
            // The address clients connect to (the one the spawned gateway registers).
            w.Prop("gatewayAddress", $"{Config.GatewayAddress}:{Config.GatewayPort}");
            w.Prop("desiredWorkers", DesiredWorkers);
            w.Prop("maxWorkers", MaxWorkers);
            w.Prop("rebalances", Rebalances);
            w.Prop("workerTimeoutSeconds", Config.WorkerTimeoutSeconds);

            // Workers: the union of control-plane rows and processes we manage (a freshly launched worker has no row yet).
            var ids = new List<string>();
            foreach (var m in _managed) if (!ids.Contains(m.Id)) ids.Add(m.Id);
            foreach (var r in workers) if (!ids.Contains(r.WorkerId)) ids.Add(r.WorkerId);
            uint totalPlayers = 0, totalBots = 0, totalServerDriven = 0, totalEntities = 0, totalAuth = 0;
            int liveCount = 0;
            w.Key("workers");
            w.BeginArray();
            foreach (var id in ids.OrderBy(IndexOf))
            {
                var row = workers.FirstOrDefault(r => r.WorkerId == id);
                var m = _managed.FirstOrDefault(x => x.Id == id);
                bool alive = row != null && ControlPlane.IsWorkerAlive(row, Config.WorkerTimeoutSeconds);
                uint index = row != null ? row.WorkerIndex : (m != null ? m.Index : 0);
                bool retiring = _retiring.ContainsKey(id);
                string state;
                if (m != null && m.RelaunchAt >= 0f) state = "relaunching";
                else if (retiring) state = "draining";
                else if (!alive && m != null && m.Handle != null && !HandleGone(m)) state = "launching";
                else if (!alive) state = "dead";
                else state = row.Status; // starting | ready
                if (alive && !retiring) liveCount++;
                if (alive)
                {
                    totalPlayers += row.PlayerCount; totalBots += row.BotCount; totalServerDriven += row.ServerDrivenCount; totalEntities += row.EntityCount; totalAuth += row.AuthoritativeCount;
                }

                w.BeginObject();
                w.Prop("id", id);
                w.Prop("index", index);
                w.Prop("color", "#" + ColorUtility.ToHtmlStringRGB(NebulaDebugOverlay.ColorForWorker((ushort)index)));
                w.Prop("state", state);
                w.Prop("alive", alive);
                w.Prop("retiring", retiring);
                w.Prop("managed", m != null);
                w.Prop("instance", m != null && m.Handle != null ? m.Handle.Describe : "");
                if (m != null && m.Handle != null) _host.WriteHandleJson(m.Handle, w);
                w.Prop("address", row != null ? $"{row.Address}:{row.Port}" : (m != null && m.Handle != null && !string.IsNullOrEmpty(m.Handle.Address) ? $"{m.Handle.Address}:{Config.WorkerBasePort + index}" : ""));
                w.Prop("heartbeatAgeSeconds", row != null ? Math.Max(0.0, (ControlPlane.Now - row.LastHeartbeat).TotalSeconds) : -1.0);
                w.Prop("relaunchInSeconds", m != null && m.RelaunchAt >= 0f ? Math.Max(0f, m.RelaunchAt - now) : -1f);
                w.Prop("drainRemainingSeconds", retiring ? Math.Max(0f, _retiring[id] - now) : -1f);
                w.Prop("tickMs", row != null ? row.TickMs : 0f);
                w.Prop("tickCount", row != null ? row.TickCount : 0UL);
                w.Prop("entities", row != null ? row.EntityCount : 0U);
                w.Prop("authoritative", row != null ? row.AuthoritativeCount : 0U);
                w.Prop("ghosts", row != null ? row.GhostCount : 0U);
                w.Prop("players", row != null ? row.PlayerCount : 0U);
                w.Prop("bots", row != null ? row.BotCount : 0U);
                w.Prop("serverDriven", row != null ? row.ServerDrivenCount : 0U);
                w.Key("containers");
                w.BeginArray();
                foreach (var l in leases.OrderBy(l => l.ContainerId, StringComparer.Ordinal))
                {
                    if (l.WorkerId == id && (l.State == LeaseState.Active || l.State == LeaseState.Draining || l.State == LeaseState.Assigning || l.State == LeaseState.Pinned)) w.Value(l.ContainerId);
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();

            w.Key("containers");
            w.BeginArray();
            foreach (var c in ContainerRegistry.All)
            {
                var l = leases.FirstOrDefault(x => x.ContainerId == c.ContainerId);
                string owner = l != null && (l.State == LeaseState.Active || l.State == LeaseState.Draining || l.State == LeaseState.Assigning) ? l.WorkerId : "";
                var ownerRow = owner != "" ? workers.FirstOrDefault(r => r.WorkerId == owner) : null;
                w.BeginObject();
                w.Prop("id", c.ContainerId);
                w.Prop("worker", owner);
                w.Prop("workerIndex", ownerRow != null ? (int)ownerRow.WorkerIndex : -1);
                w.Prop("color", "#" + ColorUtility.ToHtmlStringRGB(NebulaDebugOverlay.ColorForWorker(ownerRow != null ? (ushort)ownerRow.WorkerIndex : ushort.MaxValue)));
                w.Prop("epoch", l != null ? l.Epoch : 0UL);
                w.Prop("state", l != null ? l.State : "missing");
                if (ContainerRegistry.IsGridded)
                {
                    w.Prop("cell", $"{c.Cell.x},{c.Cell.y},{c.Cell.z}");
                    w.Prop("isCell", c.IsCell);
                }
                w.EndObject();
            }
            w.EndArray();

            // Carried containers: leases workers create for the containers their entities carry (ships, lifts).
            // They follow their carrier unless pinned to a worker of their own.
            w.Key("carried");
            w.BeginArray();
            foreach (var l in leases.Where(x => ContainerRegistry.IsDynamicId(x.ContainerId)).OrderBy(x => x.ContainerId, StringComparer.Ordinal))
            {
                bool pinned = l.State == LeaseState.Pinned;
                string owner = LeaseState.IsOwning(l.State) ? l.WorkerId : "";
                var ownerRow = owner != "" ? workers.FirstOrDefault(r => r.WorkerId == owner) : null;
                w.BeginObject();
                w.Prop("id", l.ContainerId);
                w.Prop("carrierNetId", ContainerRegistry.CarrierNetIdOf(l.ContainerId));
                w.Prop("worker", owner);
                w.Prop("workerIndex", ownerRow != null ? (int)ownerRow.WorkerIndex : -1);
                w.Prop("color", "#" + ColorUtility.ToHtmlStringRGB(NebulaDebugOverlay.ColorForWorker(ownerRow != null ? (ushort)ownerRow.WorkerIndex : ushort.MaxValue)));
                w.Prop("epoch", l.Epoch);
                w.Prop("pinned", pinned);
                w.Prop("state", pinned ? "pinned" : (owner != "" ? "following carrier" : l.State));
                w.EndObject();
            }
            w.EndArray();

            w.Key("gateways");
            w.BeginArray();
            foreach (var g in ControlPlane.Gateways)
            {
                w.BeginObject();
                w.Prop("id", g.GatewayId);
                w.Prop("address", $"{g.Address}:{g.Port}");
                w.Prop("heartbeatAgeSeconds", Math.Max(0.0, (ControlPlane.Now - g.LastHeartbeat).TotalSeconds));
                w.EndObject();
            }
            w.EndArray();

            w.Key("totals");
            w.BeginObject();
            w.Prop("liveWorkers", liveCount);
            w.Prop("players", totalPlayers);
            w.Prop("bots", totalBots);
            w.Prop("serverDriven", totalServerDriven);
            w.Prop("entities", totalEntities);
            w.Prop("authoritative", totalAuth);
            w.Prop("containers", ContainerRegistry.Count);
            w.EndObject();

            w.Key("settings");
            w.BeginObject();
            if (ControlPlane.Settings != null)
            {
                foreach (var kv in ControlPlane.Settings.OrderBy(kv => kv.Key, StringComparer.Ordinal)) w.Prop(kv.Key, kv.Value);
            }
            w.EndObject();

            w.Key("events");
            w.BeginArray();
            for (int i = _events.Count - 1; i >= 0 && i >= _events.Count - 60; i--)
            {
                var e = _events[i];
                w.BeginObject();
                w.Prop("time", e.Time.ToString("o"));
                w.Prop("level", e.Level);
                w.Prop("message", e.Message);
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
            return sb.ToString();
        }

        private uint IndexOf(string workerId)
        {
            var m = _managed.FirstOrDefault(x => x.Id == workerId);
            if (m != null) return m.Index;
            var r = ControlPlane.FindWorker(workerId);
            return r != null ? r.WorkerIndex : uint.MaxValue;
        }
    }
}
