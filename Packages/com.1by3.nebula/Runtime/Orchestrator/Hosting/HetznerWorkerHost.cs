using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula.Hosting
{
    /// <summary>
    /// Settings a cloud host needs to create a worker machine. Provider-neutral names so that a second provider
    /// reads the same switches. Read from <c>-nebula-cloud-*</c> flags with environment fallbacks.
    /// </summary>
    public sealed class CloudHostSettings
    {
        /// <summary>Provider region/location code ("ash" for Hetzner Ashburn).</summary>
        public string Location = "ash";
        /// <summary>Provider machine size ("cpx21").</summary>
        public string MachineType = "cpx21";
        /// <summary>Base OS image ("ubuntu-24.04").</summary>
        public string Image = "ubuntu-24.04";
        /// <summary>Name of the private network every machine joins; worker-to-worker traffic stays on it.</summary>
        public string Network = "nebula";
        /// <summary>Name of the SSH key installed on every machine (for log access).</summary>
        public string SshKey = "nebula";
        /// <summary>Optional provider firewall applied to worker machines.</summary>
        public string Firewall = "nebula-worker";
        /// <summary>URL the machine downloads the Linux server build from (served by the orchestrator over the private network).</summary>
        public string BuildUrl = "";
        /// <summary>Groups every machine this orchestrator owns so a new run can sweep leftovers of the previous one.</summary>
        public string MeshId = "nebula";
        /// <summary>Provider API token. Never logged.</summary>
        public string ApiToken = "";

        public static CloudHostSettings FromCommandLine(string meshId, string orchestratorPrivateAddress, ushort dashboardPort)
        {
            var s = new CloudHostSettings { MeshId = SanitizeLabel(CommandLine.Get("nebula-cloud-mesh", meshId)) };
            s.Location = CommandLine.Get("nebula-cloud-location", s.Location);
            s.MachineType = CommandLine.Get("nebula-cloud-type", s.MachineType);
            s.Image = CommandLine.Get("nebula-cloud-image", s.Image);
            s.Network = CommandLine.Get("nebula-cloud-network", s.Network);
            s.SshKey = CommandLine.Get("nebula-cloud-sshkey", s.SshKey);
            s.Firewall = CommandLine.Get("nebula-cloud-firewall", s.Firewall);
            s.BuildUrl = CommandLine.Get("nebula-build-url", $"http://{orchestratorPrivateAddress}:{dashboardPort}/build/nebula-linux.tar.gz");
            s.ApiToken = CommandLine.Get("nebula-cloud-token", Environment.GetEnvironmentVariable("HCLOUD_TOKEN") ?? "");
            return s;
        }

        /// <summary>Hetzner label values and server names: letters, digits, '-' '_' '.'</summary>
        public static string SanitizeLabel(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '-');
            }
            return sb.Length == 0 ? "nebula" : sb.ToString();
        }
    }

    /// <summary>
    /// One Hetzner Cloud VM per worker, created through the Hetzner Cloud API (https://docs.hetzner.cloud). Each VM
    /// boots Ubuntu with a cloud-init script that downloads the Linux server build from the orchestrator over the
    /// private network, reads its own private IP from the Hetzner metadata service, and starts the worker role as a
    /// systemd unit advertising that private IP. Killing a worker deletes its VM. On <see cref="Initialize"/> every VM
    /// labelled with this mesh id is deleted, so a restarted orchestrator never inherits (and pays for) stale machines.
    /// <para>
    /// All HTTP work runs on the thread pool; results are queued and applied in <see cref="Tick"/> on the main thread,
    /// where the JSON is parsed with <see cref="JsonUtility"/>.
    /// </para>
    /// </summary>
    public sealed class HetznerWorkerHost : IWorkerHost
    {
        private sealed class Handle : IWorkerHandle
        {
            public string WorkerId { get; set; }
            public WorkerHandleState State { get; set; }
            public string Reason { get; set; } = "";
            public string Address { get; set; } = "";
            public long ServerId;
            public string ServerName = "";
            public string PublicIp = "";
            public string ServerStatus = "";
            public bool DeleteRequested;
            /// <summary>Time this handle's create call went out; the start of the first billed hour.</summary>
            public float LaunchedAt;
            /// <summary>End of the hour Hetzner has already charged for, as of the moment this handle was parked.</summary>
            public float BilledUntil;
            /// <summary>When the host deletes this parked machine: the end of the paid hour, or sooner when the idle pool is shorter.</summary>
            public float RetainUntil;
            public float ParkedSecondsRemaining => State == WorkerHandleState.Parked ? Math.Max(0f, RetainUntil - Time.unscaledTime) : -1f;
            public string Describe => ServerId != 0 ? $"hetzner server {ServerId} {ServerName} {Address}" : $"hetzner {ServerName} (creating)";
        }

        private const string ApiBase = "https://api.hetzner.cloud/v1";
        private const float PollIntervalSeconds = 15f;
        /// <summary>Hetzner charges per started hour, so a machine deleted after 61 minutes has cost two.</summary>
        public const float BillingPeriodSeconds = 3600f;
        /// <summary>Delete a parked machine this long before the paid hour ends, so the delete call lands inside it.</summary>
        public const float BillingSafetyMarginSeconds = 120f;

        private readonly CloudHostSettings _settings;
        private readonly HttpClient _http;
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private readonly List<Handle> _handles = new List<Handle>();
        private Action<string, string> _log = (l, m) => { };
        private long _networkId, _sshKeyId, _firewallId;
        private int _generation;
        private float _nextPoll;
        private bool _pollInFlight;

        public HetznerWorkerHost(CloudHostSettings settings)
        {
            _settings = settings;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _http.DefaultRequestHeaders.Add("Authorization", "Bearer " + settings.ApiToken);
        }

        public string Name => "hetzner";
        /// <summary>A VM already paid for is worth keeping: a retired worker is parked and unparked instead of deleted and recreated.</summary>
        public bool SupportsParking => true;
        /// <summary>Measured: create, Ubuntu boot, build download and Unity start is well over a minute.</summary>
        public float TypicalBootSeconds => 75f;
        public bool IsReady { get; private set; }
        public string InitializationError { get; private set; } = "";

        // ---------------------------------------------------------------------------------------- initialise

        public void Initialize(Action<string, string> log)
        {
            _log = log ?? _log;
            if (string.IsNullOrEmpty(_settings.ApiToken))
            {
                InitializationError = "no Hetzner API token: set HCLOUD_TOKEN or pass -nebula-cloud-token";
                _log("error", InitializationError);
                return;
            }
            _log("info", $"hetzner: location={_settings.Location} type={_settings.MachineType} image={_settings.Image} network={_settings.Network} mesh={_settings.MeshId} build={_settings.BuildUrl}");
            Task.Run(async () =>
            {
                try
                {
                    var net = await GetAsync($"/networks?name={Uri.EscapeDataString(_settings.Network)}");
                    var key = await GetAsync($"/ssh_keys?name={Uri.EscapeDataString(_settings.SshKey)}");
                    var fw = await GetAsync($"/firewalls?name={Uri.EscapeDataString(_settings.Firewall)}");
                    var stale = await GetAsync($"/servers?label_selector={Uri.EscapeDataString("nebula-mesh=" + _settings.MeshId + ",nebula-role=worker")}&per_page=50");
                    _mainThread.Enqueue(() => FinishInitialize(net, key, fw, stale));
                }
                catch (Exception e)
                {
                    _mainThread.Enqueue(() => Fail("hetzner API unreachable: " + e.Message));
                }
            });
        }

        private void FinishInitialize(ApiResult net, ApiResult key, ApiResult fw, ApiResult stale)
        {
            if (!net.Ok) { Fail("listing networks: " + net.Error); return; }
            if (!key.Ok) { Fail("listing ssh keys: " + key.Error); return; }
            if (!stale.Ok) { Fail("listing servers: " + stale.Error); return; }
            var networks = JsonUtility.FromJson<NetworksResponse>(net.Body)?.networks;
            var n = networks?.FirstOrDefault(x => x.name == _settings.Network);
            if (n == null) { Fail($"private network '{_settings.Network}' does not exist in this Hetzner project (`nebula deploy` creates it)"); return; }
            _networkId = n.id;
            var keys = JsonUtility.FromJson<SshKeysResponse>(key.Body)?.ssh_keys;
            var k = keys?.FirstOrDefault(x => x.name == _settings.SshKey);
            if (k == null) { Fail($"ssh key '{_settings.SshKey}' does not exist in this Hetzner project"); return; }
            _sshKeyId = k.id;
            if (fw.Ok)
            {
                var f = JsonUtility.FromJson<FirewallsResponse>(fw.Body)?.firewalls?.FirstOrDefault(x => x.name == _settings.Firewall);
                _firewallId = f?.id ?? 0;
            }
            var servers = JsonUtility.FromJson<ServersResponse>(stale.Body)?.servers ?? new List<Server>();
            foreach (var s in servers)
            {
                // Parked machines carry the same labels as running ones, so a crashed orchestrator's idle pool is
                // swept here too and no unowned machine keeps billing.
                _log("warn", $"hetzner: deleting stale server {s.id} {s.name} left by a previous run");
                Delete(s.id, s.name);
            }
            IsReady = true;
            _log("info", $"hetzner: ready (network {_networkId}, ssh key {_sshKeyId}{(_firewallId != 0 ? $", firewall {_firewallId}" : "")})");
        }

        private void Fail(string message)
        {
            InitializationError = message;
            _log("error", "hetzner: " + message);
        }

        // ---------------------------------------------------------------------------------------- launch / kill

        public IWorkerHandle Launch(WorkerLaunchSpec spec)
        {
            var h = new Handle { WorkerId = spec.WorkerId, State = WorkerHandleState.Launching };
            if (!IsReady)
            {
                h.State = WorkerHandleState.Failed;
                h.Reason = string.IsNullOrEmpty(InitializationError) ? "host not ready" : InitializationError;
                return h;
            }
            h.ServerName = $"{_settings.MeshId}-{spec.WorkerId}-{++_generation}";
            h.LaunchedAt = Time.unscaledTime;
            _handles.Add(h);

            string workerArgs = $"-nebula-role worker -nebula-worker-id {spec.WorkerId} -nebula-worker-index {spec.Index} -nebula-port {spec.Port} -nebula-advertise $PRIVATE_IP";
            workerArgs += " " + spec.CommonArgs;
            string userData = BuildCloudInit(_settings.BuildUrl, workerArgs);

            var sb = new StringBuilder();
            var w = new JsonWriter(sb);
            w.BeginObject();
            w.Prop("name", h.ServerName);
            w.Prop("server_type", _settings.MachineType);
            w.Prop("image", _settings.Image);
            w.Prop("location", _settings.Location);
            w.Prop("start_after_create", true);
            w.Prop("user_data", userData);
            w.Key("ssh_keys"); w.BeginArray(); w.Value(_sshKeyId); w.EndArray();
            w.Key("networks"); w.BeginArray(); w.Value(_networkId); w.EndArray();
            if (_firewallId != 0)
            {
                w.Key("firewalls"); w.BeginArray(); w.BeginObject(); w.Prop("firewall", _firewallId); w.EndObject(); w.EndArray();
            }
            w.Key("public_net"); w.BeginObject(); w.Prop("enable_ipv4", true); w.Prop("enable_ipv6", false); w.EndObject();
            w.Key("labels"); w.BeginObject();
            w.Prop("nebula-mesh", _settings.MeshId);
            w.Prop("nebula-role", "worker");
            w.Prop("nebula-worker", spec.WorkerId);
            w.EndObject();
            w.EndObject();
            string body = sb.ToString();

            _log("info", $"hetzner: creating server {h.ServerName} ({_settings.MachineType} in {_settings.Location}) for {spec.WorkerId}");
            Task.Run(async () =>
            {
                var r = await SendAsync(HttpMethod.Post, "/servers", body);
                _mainThread.Enqueue(() => OnCreated(h, r));
            });
            return h;
        }

        private void OnCreated(Handle h, ApiResult r)
        {
            if (h.DeleteRequested)
            {
                // Killed while the create call was in flight: delete whatever came back.
                if (r.Ok)
                {
                    var created = JsonUtility.FromJson<ServerResponse>(r.Body)?.server;
                    if (created != null) Delete(created.id, created.name);
                }
                return;
            }
            if (!r.Ok)
            {
                h.State = WorkerHandleState.Failed;
                h.Reason = r.Error;
                _handles.Remove(h);
                _log("error", $"hetzner: creating {h.ServerName} failed: {r.Error}");
                return;
            }
            var s = JsonUtility.FromJson<ServerResponse>(r.Body)?.server;
            if (s == null)
            {
                h.State = WorkerHandleState.Failed;
                h.Reason = "unexpected create response";
                _handles.Remove(h);
                return;
            }
            Apply(h, s);
            h.State = WorkerHandleState.Running;
            _log("info", $"hetzner: {h.WorkerId} is server {s.id} {s.name} private {h.Address} public {h.PublicIp}; booting");
        }

        private static void Apply(Handle h, Server s)
        {
            h.ServerId = s.id;
            h.ServerName = s.name;
            h.ServerStatus = s.status ?? "";
            h.PublicIp = s.public_net?.ipv4?.ip ?? h.PublicIp;
            var priv = s.private_net?.FirstOrDefault();
            if (priv != null && !string.IsNullOrEmpty(priv.ip)) h.Address = priv.ip;
        }

        public void Kill(IWorkerHandle handle)
        {
            if (!(handle is Handle h) || h.DeleteRequested) return;
            h.DeleteRequested = true;
            if (h.State == WorkerHandleState.Running || h.State == WorkerHandleState.Launching)
            {
                h.State = WorkerHandleState.Exited;
                h.Reason = "deleted";
            }
            _handles.Remove(h);
            if (h.ServerId != 0) Delete(h.ServerId, h.ServerName);
        }

        /// <summary>
        /// End of the billing period a machine launched at <paramref name="launchedAt"/> has been charged for as of
        /// <paramref name="now"/>. Hetzner bills per started hour, so a machine up for five minutes is paid for until
        /// minute 60 and one up for 61 minutes until minute 120; keeping it parked until then is free. Pure function.
        /// </summary>
        public static float BilledUntilSeconds(float launchedAt, float now, float billingPeriodSeconds = BillingPeriodSeconds)
        {
            if (billingPeriodSeconds <= 0f) return now;
            double uptime = Math.Max(0.0, now - launchedAt);
            double periods = Math.Max(1.0, Math.Ceiling(uptime / billingPeriodSeconds));
            return (float)(launchedAt + periods * billingPeriodSeconds);
        }

        /// <summary>
        /// When a machine parked at <paramref name="now"/> stops being kept: the end of the hour it has already been
        /// billed, or sooner when <paramref name="idlePoolSeconds"/> asks for less. A longer idle pool is not
        /// honoured, because the hour after this one is a fresh charge and nobody asked to pay it.
        /// <para>
        /// <see cref="BillingSafetyMarginSeconds"/> is taken off the <i>billed</i> deadline only, so the delete call
        /// lands before the next hour starts. It is not taken off a retention the developer asked for: an
        /// <c>IdlePoolSeconds</c> under the margin would otherwise mean "delete on the next tick".
        /// </para>
        /// Pure function; the result is the wall-clock moment the machine goes.
        /// </summary>
        public static float RetainUntilSeconds(float launchedAt, float now, float idlePoolSeconds)
        {
            float billedUntil = BilledUntilSeconds(launchedAt, now) - BillingSafetyMarginSeconds;
            if (idlePoolSeconds <= 0f) return billedUntil;
            return Math.Min(billedUntil, now + idlePoolSeconds);
        }

        /// <summary>True when a parked machine kept until <paramref name="retainUntil"/> must be deleted now. Pure function.</summary>
        public static bool ParkedInstanceExpired(float now, float retainUntil) => now >= retainUntil;

        /// <summary>
        /// Keep the machine instead of deleting it: the worker process stays up and keeps heartbeating, the
        /// orchestrator simply deals it nothing. It is deleted in <see cref="Tick"/> once the hour Hetzner has
        /// already charged for runs out (or sooner, when <paramref name="idlePoolSeconds"/> is shorter), unless
        /// <see cref="Unpark"/> claims it first.
        /// </summary>
        public void Park(IWorkerHandle handle, float idlePoolSeconds)
        {
            if (!(handle is Handle h) || h.DeleteRequested) return;
            if (h.State != WorkerHandleState.Running && h.State != WorkerHandleState.Launching) { Kill(handle); return; }
            float now = Time.unscaledTime;
            h.BilledUntil = BilledUntilSeconds(h.LaunchedAt, now);
            h.RetainUntil = RetainUntilSeconds(h.LaunchedAt, now, idlePoolSeconds);
            h.State = WorkerHandleState.Parked;
            h.Reason = "parked";
            _log("info", $"hetzner: parking server {h.ServerId} {h.ServerName} ({h.WorkerId}); it stays paid for another {Math.Max(0f, h.RetainUntil - now):F0}s");
        }

        /// <summary>Take a parked machine back into service. The worker never stopped, so it is ready at once.</summary>
        public bool Unpark(IWorkerHandle handle)
        {
            if (!(handle is Handle h) || h.State != WorkerHandleState.Parked || h.DeleteRequested) return false;
            h.State = WorkerHandleState.Running;
            h.Reason = "";
            _log("info", $"hetzner: unparking server {h.ServerId} {h.ServerName}; it keeps worker id {h.WorkerId}");
            return true;
        }

        private void Delete(long serverId, string name)
        {
            Task.Run(async () =>
            {
                var r = await SendAsync(HttpMethod.Delete, $"/servers/{serverId}", null);
                _mainThread.Enqueue(() =>
                {
                    if (r.Ok || r.Status == 404) _log("info", $"hetzner: deleted server {serverId} {name}");
                    else _log("error", $"hetzner: deleting server {serverId} {name} failed: {r.Error} (delete it in the Hetzner console to stop billing)");
                });
            });
        }

        // ---------------------------------------------------------------------------------------- tick

        public void Tick()
        {
            while (_mainThread.TryDequeue(out var a))
            {
                try { a(); } catch (Exception e) { _log("error", "hetzner: " + e.Message); }
            }
            ExpireParked();
            if (IsReady && !_pollInFlight && Time.unscaledTime >= _nextPoll && _handles.Any(h => h.ServerId != 0))
            {
                _nextPoll = Time.unscaledTime + PollIntervalSeconds;
                _pollInFlight = true;
                Task.Run(async () =>
                {
                    var r = await GetAsync($"/servers?label_selector={Uri.EscapeDataString("nebula-mesh=" + _settings.MeshId + ",nebula-role=worker")}&per_page=50");
                    _mainThread.Enqueue(() => OnPolled(r));
                });
            }
        }

        /// <summary>Delete parked machines whose paid hour is nearly over: nobody unparked them, so they stop costing.</summary>
        private void ExpireParked()
        {
            if (_handles.Count == 0) return;
            float now = Time.unscaledTime;
            // Backwards, so removing the expired machine does not disturb the walk: this runs every frame and must
            // not copy the handle list to do nothing, which is the usual outcome.
            for (int i = _handles.Count - 1; i >= 0; i--)
            {
                var h = _handles[i];
                if (h.State != WorkerHandleState.Parked || h.DeleteRequested) continue;
                if (!ParkedInstanceExpired(now, h.RetainUntil)) continue;
                _log("info", $"hetzner: parked server {h.ServerId} {h.ServerName} ({h.WorkerId}) reached the end of its paid hour; deleting it");
                h.DeleteRequested = true;
                h.State = WorkerHandleState.Exited;
                h.Reason = "idle pool expired";
                _handles.RemoveAt(i);
                if (h.ServerId != 0) Delete(h.ServerId, h.ServerName);
            }
        }

        private void OnPolled(ApiResult r)
        {
            _pollInFlight = false;
            if (!r.Ok) { _log("warn", "hetzner: listing servers failed: " + r.Error); return; }
            var servers = JsonUtility.FromJson<ServersResponse>(r.Body)?.servers ?? new List<Server>();
            foreach (var h in _handles.ToList())
            {
                if (h.ServerId == 0) continue;
                var s = servers.FirstOrDefault(x => x.id == h.ServerId);
                if (s == null)
                {
                    h.State = WorkerHandleState.Exited;
                    h.Reason = "server no longer exists";
                    _handles.Remove(h);
                    _log("warn", $"hetzner: server {h.ServerId} {h.ServerName} ({h.WorkerId}) disappeared");
                    continue;
                }
                Apply(h, s);
                if (s.status == "off" || s.status == "deleting")
                {
                    h.State = WorkerHandleState.Exited;
                    h.Reason = "server " + s.status;
                    _handles.Remove(h);
                    _log("warn", $"hetzner: server {h.ServerId} {h.ServerName} ({h.WorkerId}) is {s.status}");
                }
            }
        }

        public void WriteHandleJson(IWorkerHandle handle, JsonWriter w)
        {
            if (!(handle is Handle h)) return;
            w.Prop("serverId", h.ServerId);
            w.Prop("serverName", h.ServerName);
            w.Prop("serverStatus", h.ServerStatus);
            w.Prop("publicIp", h.PublicIp);
            w.Prop("privateIp", h.Address);
            if (h.State == WorkerHandleState.Parked) w.Prop("paidSecondsRemaining", h.ParkedSecondsRemaining);
        }

        public void Dispose()
        {
            // Best effort: fire the deletes and give them a moment. The next orchestrator run sweeps anything missed.
            var ids = _handles.Where(h => h.ServerId != 0).Select(h => (h.ServerId, h.ServerName)).ToList();
            _handles.Clear();
            if (ids.Count > 0)
            {
                var tasks = ids.Select(t => SendAsync(HttpMethod.Delete, $"/servers/{t.ServerId}", null)).ToArray();
                try { Task.WaitAll(tasks, TimeSpan.FromSeconds(10)); } catch { }
            }
            _http.Dispose();
        }

        // ---------------------------------------------------------------------------------------- cloud-init

        /// <summary>
        /// Boot script for a worker VM. Waits for the private interface, fetches the build from the orchestrator,
        /// and runs the worker as a systemd unit. <c>$PRIVATE_IP</c> in <paramref name="workerArgs"/> expands on the machine.
        /// </summary>
        public static string BuildCloudInit(string buildUrl, string workerArgs)
        {
            return string.Join("\n", new[]
            {
                "#!/bin/bash",
                "set -u",
                "exec > /var/log/nebula-bootstrap.log 2>&1",
                "echo \"[nebula] bootstrap start $(date -u +%FT%TZ)\"",
                "mkdir -p /opt/nebula && cd /opt/nebula",
                "PRIVATE_IP=\"\"",
                "for i in $(seq 1 60); do",
                // Metadata is YAML: \"- ip: 10.0.1.4\" is the first line of the first (only) private network.
                "  PRIVATE_IP=$(curl -fsS --max-time 3 http://169.254.169.254/hetzner/v1/metadata/private-networks 2>/dev/null | awk '/^[- ]*ip:/ {print $NF; exit}')",
                "  [ -z \"$PRIVATE_IP\" ] && PRIVATE_IP=$(hostname -I | tr ' ' '\\n' | grep -m1 '^10\\.')",
                "  [ -n \"$PRIVATE_IP\" ] && break",
                "  sleep 2",
                "done",
                "echo \"[nebula] private ip: $PRIVATE_IP\"",
                "for i in $(seq 1 90); do",
                "  curl -fsS --max-time 120 -o build.tar.gz \"" + buildUrl + "\" && break",
                "  echo \"[nebula] build download attempt $i failed\"; sleep 5",
                "done",
                "tar xzf build.tar.gz && chmod +x /opt/nebula/Nebula.x86_64",
                "echo \"[nebula] build unpacked $(date -u +%FT%TZ)\"",
                "cat > /etc/systemd/system/nebula-worker.service <<UNIT",
                "[Unit]",
                "Description=Nebula worker",
                "After=network-online.target",
                "",
                "[Service]",
                "WorkingDirectory=/opt/nebula",
                "ExecStart=/opt/nebula/Nebula.x86_64 -batchmode -nographics " + workerArgs + " -logFile /var/log/nebula-worker.log",
                "Restart=no",
                "LimitNOFILE=65536",
                "",
                "[Install]",
                "WantedBy=multi-user.target",
                "UNIT",
                "systemctl daemon-reload",
                "systemctl start nebula-worker",
                "echo \"[nebula] worker started $(date -u +%FT%TZ)\"",
                "",
            });
        }

        // ---------------------------------------------------------------------------------------- http

        private struct ApiResult
        {
            public int Status;
            public string Body;
            public bool Ok => Status >= 200 && Status < 300;
            public string Error
            {
                get
                {
                    if (Status == 0) return Body;
                    try
                    {
                        var e = JsonUtility.FromJson<ErrorResponse>(Body)?.error;
                        if (e != null && !string.IsNullOrEmpty(e.message)) return $"HTTP {Status} {e.code}: {e.message}";
                    }
                    catch { }
                    return $"HTTP {Status}";
                }
            }
        }

        private Task<ApiResult> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

        private async Task<ApiResult> SendAsync(HttpMethod method, string path, string jsonBody)
        {
            try
            {
                using (var req = new HttpRequestMessage(method, ApiBase + path))
                {
                    if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                    {
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return new ApiResult { Status = (int)resp.StatusCode, Body = body };
                    }
                }
            }
            catch (Exception e)
            {
                return new ApiResult { Status = 0, Body = e.Message };
            }
        }

        // ---------------------------------------------------------------------------------------- api shapes (JsonUtility)
#pragma warning disable 649
        [Serializable] private class Ipv4 { public string ip; }
        [Serializable] private class PublicNet { public Ipv4 ipv4; }
        [Serializable] private class PrivateNet { public long network; public string ip; }
        [Serializable] private class Server { public long id; public string name; public string status; public PublicNet public_net; public List<PrivateNet> private_net; }
        [Serializable] private class ServerResponse { public Server server; }
        [Serializable] private class ServersResponse { public List<Server> servers; }
        [Serializable] private class Named { public long id; public string name; }
        [Serializable] private class NetworksResponse { public List<Named> networks; }
        [Serializable] private class SshKeysResponse { public List<Named> ssh_keys; }
        [Serializable] private class FirewallsResponse { public List<Named> firewalls; }
        [Serializable] private class ApiError { public string code; public string message; }
        [Serializable] private class ErrorResponse { public ApiError error; }
#pragma warning restore 649
    }
}
