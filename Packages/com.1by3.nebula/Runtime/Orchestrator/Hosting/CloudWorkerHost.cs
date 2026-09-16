using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
#if NEBULA_SERVICE
using Nebula.ServicePrimitives;
#else
using UnityEngine;
#endif

namespace Nebula.Hosting
{
    /// <summary>
    /// Where a managed deployment's orchestrator asks for worker machines: a resource API scoped to this one
    /// deployment, reached with a token that can do nothing else. Read from <c>-nebula-cloud-api</c> and
    /// <c>-nebula-cloud-deployment-token</c>, with <c>NEBULA_CLOUD_API</c> and <c>NEBULA_CLOUD_DEPLOYMENT_TOKEN</c>
    /// as environment fallbacks (a systemd EnvironmentFile keeps the token off the command line).
    /// </summary>
    public sealed class CloudWorkerHostSettings
    {
        /// <summary>Base URL of the resource API, for example <c>https://api.example.com/v1</c>. The host appends <c>/d/…</c>.</summary>
        public string ApiUrl = "";
        /// <summary>The deployment-scoped bearer token. Never logged.</summary>
        public string DeploymentToken = "";
        /// <summary>How often the worker list is refreshed.</summary>
        public float PollIntervalSeconds = 5f;

        public static CloudWorkerHostSettings FromCommandLine()
        {
            return new CloudWorkerHostSettings
            {
                ApiUrl = CommandLine.Get("nebula-cloud-api", Environment.GetEnvironmentVariable("NEBULA_CLOUD_API") ?? ""),
                DeploymentToken = CommandLine.Get("nebula-cloud-deployment-token", Environment.GetEnvironmentVariable("NEBULA_CLOUD_DEPLOYMENT_TOKEN") ?? ""),
            };
        }
    }

    /// <summary>
    /// An <see cref="IWorkerHost"/> for managed hosting: instead of calling a cloud provider, the orchestrator asks a
    /// deployment-scoped resource API for a worker (<c>POST /d/workers</c>), polls the list for its state and
    /// private address, and deletes, parks and unparks through the same API. The service behind it holds the provider
    /// credentials and turns each request into a machine; the orchestrator's machine never sees an account-wide
    /// credential. Nebula's own assignment, drain, replacement and scale-to-zero logic is untouched: this host only
    /// changes where a worker comes from.
    /// <para>
    /// The API is documented by the hosting platform; the shapes this class relies on are: a worker resource
    /// <c>{workerId, resourceId, index, state, privateAddress, size, reason, parkedUntil}</c> with states
    /// <c>launching | running | parked | exiting | exited | failed</c>, wrapped as <c>{"worker": …}</c> on single
    /// answers and <c>{"workers": […]}</c> on the list, and an error envelope <c>{"error": {"code", "message"}}</c>.
    /// A create answers 402 when the deployment reached its spend limit and 409 when it is at its worker ceiling.
    /// </para>
    /// All HTTP work runs on the thread pool; results are applied in <see cref="Tick"/> on the main thread.
    /// </summary>
    public sealed class CloudWorkerHost : IWorkerHost
    {
        private sealed class Handle : IWorkerHandle
        {
            public string WorkerId { get; set; }
            public WorkerHandleState State { get; set; }
            public string Reason { get; set; } = "";
            public string Address { get; set; } = "";
            public string ResourceId = "";
            public string Size = "";
            public bool DeleteRequested;
            /// <summary>Wall-clock end of the parked period the API promised, as Time.unscaledTime; negative when unknown.</summary>
            public float ParkedUntil = -1f;
            public float ParkedSecondsRemaining => State == WorkerHandleState.Parked && ParkedUntil >= 0 ? Math.Max(0f, ParkedUntil - Time.unscaledTime) : -1f;
            public string Describe => string.IsNullOrEmpty(ResourceId) ? $"cloud {WorkerId} (requesting)" : $"cloud {ResourceId} {Address}";
        }

        private readonly CloudWorkerHostSettings _settings;
        private readonly HttpClient _http;
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private readonly List<Handle> _handles = new List<Handle>();
        private Action<string, string> _log = (l, m) => { };
        private float _nextPoll;
        private bool _pollInFlight;

        public CloudWorkerHost(CloudWorkerHostSettings settings)
        {
            _settings = settings;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _http.DefaultRequestHeaders.Add("Authorization", "Bearer " + settings.DeploymentToken);
            _http.DefaultRequestHeaders.Add("User-Agent", "nebula-orchestrator");
        }

        public string Name => "cloud";
        /// <summary>The platform keeps a parked machine until the moment it stops being free, then deletes it itself; parking is always worth it.</summary>
        public bool SupportsParking => true;
        /// <summary>A machine that boots an image and downloads a build: over a minute.</summary>
        public float TypicalBootSeconds => 75f;
        public bool IsReady { get; private set; }
        public string InitializationError { get; private set; } = "";

        // ---------------------------------------------------------------------------------------- initialise

        public void Initialize(Action<string, string> log)
        {
            _log = log ?? _log;
            if (string.IsNullOrEmpty(_settings.ApiUrl) || string.IsNullOrEmpty(_settings.DeploymentToken))
            {
                InitializationError = "no resource API: set -nebula-cloud-api and -nebula-cloud-deployment-token (or NEBULA_CLOUD_API / NEBULA_CLOUD_DEPLOYMENT_TOKEN)";
                _log("error", "cloud: " + InitializationError);
                return;
            }
            _log("info", $"cloud: resource API {_settings.ApiUrl}");
            Task.Run(async () =>
            {
                var self = await GetAsync("/d/self");
                var list = self.Ok ? await GetAsync("/d/workers") : default;
                _mainThread.Enqueue(() => FinishInitialize(self, list));
            });
        }

        private void FinishInitialize(ApiResult self, ApiResult list)
        {
            if (!self.Ok) { Fail("the resource API refused this deployment's token: " + self.Error); return; }
            if (!list.Ok) { Fail("listing workers: " + list.Error); return; }
            var info = JsonUtility.FromJson<SelfResponse>(self.Body);
            var workers = JsonUtility.FromJson<WorkersResponse>(list.Body)?.workers ?? new List<Worker>();
            foreach (var w in workers)
            {
                // A previous orchestrator's machines: it is gone, so are its assignments; they are swept rather than adopted.
                if (w.state == "exited" || w.state == "exiting" || w.state == "failed") continue;
                _log("warn", $"cloud: deleting worker {w.workerId} ({w.resourceId}) left by a previous run");
                Delete(w.workerId);
            }
            IsReady = true;
            _log("info", $"cloud: ready (deployment {info?.deploymentId}, region {info?.region}, worker size {info?.workerSize}, up to {info?.maxWorkers} workers{(info != null && info.spendLimited ? "; SPEND LIMIT REACHED: no new workers" : "")})");
        }

        private void Fail(string message)
        {
            InitializationError = message;
            _log("error", "cloud: " + message);
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
            _handles.Add(h);
            var sb = new StringBuilder();
            var w = new JsonWriter(sb);
            w.BeginObject();
            w.Prop("workerId", spec.WorkerId);
            w.Prop("index", (long)spec.Index);
            w.Prop("port", (long)spec.Port);
            w.Prop("commonArgs", spec.CommonArgs ?? "");
            w.EndObject();
            string body = sb.ToString();
            _log("info", $"cloud: requesting a worker machine for {spec.WorkerId}");
            Task.Run(async () =>
            {
                var r = await SendAsync(HttpMethod.Post, "/d/workers", body);
                _mainThread.Enqueue(() => OnCreated(h, r));
            });
            return h;
        }

        private void OnCreated(Handle h, ApiResult r)
        {
            if (h.DeleteRequested)
            {
                if (r.Ok) Delete(h.WorkerId);
                return;
            }
            if (!r.Ok)
            {
                h.State = WorkerHandleState.Failed;
                h.Reason = r.Status == 402 ? "spend limit reached" : r.Status == 409 ? "deployment is at its worker ceiling" : r.Error;
                _handles.Remove(h);
                _log("error", $"cloud: requesting {h.WorkerId} failed: {h.Reason}");
                return;
            }
            var created = JsonUtility.FromJson<WorkerResponse>(r.Body)?.worker;
            if (created == null)
            {
                h.State = WorkerHandleState.Failed;
                h.Reason = "unexpected create response";
                _handles.Remove(h);
                return;
            }
            Apply(h, created);
            _log("info", $"cloud: {h.WorkerId} is {h.ResourceId} ({h.Size}); {created.state}");
        }

        /// <summary>The API's state for a worker, onto the orchestrator's handle states.</summary>
        public static WorkerHandleState MapState(string state, WorkerHandleState current)
        {
            switch (state)
            {
                case "launching": return WorkerHandleState.Launching;
                case "running": return WorkerHandleState.Running;
                case "parked": return WorkerHandleState.Parked;
                case "exiting":
                case "exited": return WorkerHandleState.Exited;
                case "failed": return WorkerHandleState.Failed;
                default: return current;
            }
        }

        private void Apply(Handle h, Worker w)
        {
            if (!string.IsNullOrEmpty(w.resourceId)) h.ResourceId = w.resourceId;
            if (!string.IsNullOrEmpty(w.privateAddress)) h.Address = w.privateAddress;
            if (!string.IsNullOrEmpty(w.size)) h.Size = w.size;
            var state = MapState(w.state, h.State);
            if (state == WorkerHandleState.Exited || state == WorkerHandleState.Failed) h.Reason = string.IsNullOrEmpty(w.reason) ? w.state : w.reason;
            if (state == WorkerHandleState.Parked && !string.IsNullOrEmpty(w.parkedUntil) && DateTime.TryParse(w.parkedUntil, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var until))
                h.ParkedUntil = Time.unscaledTime + (float)(until - DateTime.UtcNow).TotalSeconds;
            h.State = state;
        }

        public void Kill(IWorkerHandle handle)
        {
            if (!(handle is Handle h) || h.DeleteRequested) return;
            h.DeleteRequested = true;
            if (h.State != WorkerHandleState.Failed)
            {
                h.State = WorkerHandleState.Exited;
                h.Reason = "deleted";
            }
            _handles.Remove(h);
            Delete(h.WorkerId);
        }

        public void Park(IWorkerHandle handle, float idlePoolSeconds)
        {
            if (!(handle is Handle h) || h.DeleteRequested) return;
            if (h.State != WorkerHandleState.Running && h.State != WorkerHandleState.Launching) { Kill(handle); return; }
            h.State = WorkerHandleState.Parked;
            h.Reason = "parked";
            h.ParkedUntil = idlePoolSeconds > 0 ? Time.unscaledTime + idlePoolSeconds : -1f;
            _log("info", $"cloud: parking {h.WorkerId} ({h.ResourceId})" + (idlePoolSeconds > 0 ? $" for up to {idlePoolSeconds:F0}s" : " until it stops being free"));
            string body = "{\"seconds\":" + Math.Max(0, (long)idlePoolSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
            Task.Run(async () =>
            {
                var r = await SendAsync(HttpMethod.Post, $"/d/workers/{Uri.EscapeDataString(h.WorkerId)}/park", body);
                _mainThread.Enqueue(() =>
                {
                    if (!r.Ok) { _log("warn", $"cloud: parking {h.WorkerId} failed: {r.Error}"); return; }
                    var w = JsonUtility.FromJson<WorkerResponse>(r.Body)?.worker;
                    if (w != null && !h.DeleteRequested) Apply(h, w);
                });
            });
        }

        /// <summary>
        /// Take a parked machine back. The machine never stopped, so the handle is Running at once; should the API
        /// answer that it already deleted the machine (410), the handle turns Exited and the orchestrator launches a fresh worker.
        /// </summary>
        public bool Unpark(IWorkerHandle handle)
        {
            if (!(handle is Handle h) || h.State != WorkerHandleState.Parked || h.DeleteRequested) return false;
            h.State = WorkerHandleState.Running;
            h.Reason = "";
            h.ParkedUntil = -1f;
            _log("info", $"cloud: unparking {h.WorkerId} ({h.ResourceId}); it keeps its worker id");
            Task.Run(async () =>
            {
                var r = await SendAsync(HttpMethod.Post, $"/d/workers/{Uri.EscapeDataString(h.WorkerId)}/unpark", "{}");
                _mainThread.Enqueue(() =>
                {
                    if (h.DeleteRequested) return;
                    if (r.Status == 410 || r.Status == 404)
                    {
                        h.State = WorkerHandleState.Exited;
                        h.Reason = "the parked machine was already released";
                        _handles.Remove(h);
                        _log("warn", $"cloud: {h.WorkerId} was gone when unparked; a fresh worker will be launched");
                        return;
                    }
                    if (!r.Ok) { _log("warn", $"cloud: unparking {h.WorkerId} failed: {r.Error}"); return; }
                    var w = JsonUtility.FromJson<WorkerResponse>(r.Body)?.worker;
                    if (w != null) Apply(h, w);
                });
            });
            return true;
        }

        private void Delete(string workerId)
        {
            Task.Run(async () =>
            {
                var r = await SendAsync(HttpMethod.Delete, $"/d/workers/{Uri.EscapeDataString(workerId)}", null);
                _mainThread.Enqueue(() =>
                {
                    if (r.Ok || r.Status == 404 || r.Status == 410) _log("info", $"cloud: released worker {workerId}");
                    else _log("error", $"cloud: releasing worker {workerId} failed: {r.Error} (the platform's sweeper will retry)");
                });
            });
        }

        // ---------------------------------------------------------------------------------------- tick

        public void Tick()
        {
            while (_mainThread.TryDequeue(out var a))
            {
                try { a(); } catch (Exception e) { _log("error", "cloud: " + e.Message); }
            }
            if (IsReady && !_pollInFlight && Time.unscaledTime >= _nextPoll && _handles.Count > 0)
            {
                _nextPoll = Time.unscaledTime + _settings.PollIntervalSeconds;
                _pollInFlight = true;
                Task.Run(async () =>
                {
                    var r = await GetAsync("/d/workers");
                    _mainThread.Enqueue(() => OnPolled(r));
                });
            }
        }

        private void OnPolled(ApiResult r)
        {
            _pollInFlight = false;
            if (!r.Ok) { _log("warn", "cloud: listing workers failed: " + r.Error); return; }
            var workers = JsonUtility.FromJson<WorkersResponse>(r.Body)?.workers ?? new List<Worker>();
            foreach (var h in _handles.ToList())
            {
                if (string.IsNullOrEmpty(h.ResourceId)) continue; // the create call has not answered yet
                var w = workers.FirstOrDefault(x => x.workerId == h.WorkerId);
                if (w == null)
                {
                    h.State = WorkerHandleState.Exited;
                    h.Reason = "no longer listed by the resource API";
                    _handles.Remove(h);
                    _log("warn", $"cloud: {h.WorkerId} ({h.ResourceId}) disappeared");
                    continue;
                }
                var before = h.State;
                Apply(h, w);
                if (h.State != before)
                {
                    if (h.State == WorkerHandleState.Exited || h.State == WorkerHandleState.Failed)
                    {
                        _handles.Remove(h);
                        _log("warn", $"cloud: {h.WorkerId} ({h.ResourceId}) is {w.state}: {h.Reason}");
                    }
                    else _log("info", $"cloud: {h.WorkerId} ({h.ResourceId}) is {w.state} at {h.Address}");
                }
            }
        }

        public void WriteHandleJson(IWorkerHandle handle, JsonWriter w)
        {
            if (!(handle is Handle h)) return;
            w.Prop("resourceId", h.ResourceId);
            w.Prop("privateAddress", h.Address);
            w.Prop("size", h.Size);
        }

        public void Dispose() => _http.Dispose();

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
                using (var req = new HttpRequestMessage(method, _settings.ApiUrl.TrimEnd('/') + path))
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
        [Serializable] private class Worker { public string workerId; public string resourceId; public long index; public string state; public string privateAddress; public string size; public string reason; public string parkedUntil; }
        [Serializable] private class WorkerResponse { public Worker worker; }
        [Serializable] private class WorkersResponse { public List<Worker> workers; }
        [Serializable] private class SelfResponse { public string deploymentId; public string region; public string workerSize; public long maxWorkers; public bool spendLimited; }
        [Serializable] private class ApiError { public string code; public string message; }
        [Serializable] private class ErrorResponse { public ApiError error; }
#pragma warning restore 649
    }
}
