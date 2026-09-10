using System;
using System.Diagnostics;
using System.IO;

namespace Nebula.Hosting
{
    /// <summary>
    /// Runs every worker as a child process of the orchestrator, from the same executable. This is the local mesh
    /// (<c>nebula start</c>) and the reference implementation of <see cref="IWorkerHost"/>. The orchestrator
    /// also uses it to start co-located services (the gateway) whatever host the workers use.
    /// </summary>
    public sealed class ProcessWorkerHost : IWorkerHost
    {
        private sealed class Handle : IWorkerHandle
        {
            public string WorkerId { get; set; }
            public Process Process;
            public WorkerHandleState State { get; set; }
            public string Reason { get; set; } = "";
            public string Address => "";
            public string Describe => Process != null ? $"pid {SafePid(Process)}" : "no process";
        }

        private readonly string _executable;
        private readonly string _advertiseAddress;
        private readonly System.Collections.Generic.List<Handle> _handles = new System.Collections.Generic.List<Handle>();
        private Action<string, string> _log = (l, m) => { };

        /// <param name="executable">Player executable to run; empty = the orchestrator's own.</param>
        /// <param name="advertiseAddress">Address child workers advertise to peers (127.0.0.1 on one machine).</param>
        public ProcessWorkerHost(string executable, string advertiseAddress)
        {
            _executable = executable;
            _advertiseAddress = string.IsNullOrEmpty(advertiseAddress) ? "127.0.0.1" : advertiseAddress;
        }

        public string Name => "process";
        public bool IsReady => true;
        public string InitializationError => "";

        public void Initialize(Action<string, string> log) { _log = log ?? _log; }

        public IWorkerHandle Launch(WorkerLaunchSpec spec)
        {
            string args = $"-nebula-role worker -nebula-worker-id {spec.WorkerId} -nebula-worker-index {spec.Index} -nebula-port {spec.Port} -nebula-advertise {_advertiseAddress}";
            var h = new Handle { WorkerId = spec.WorkerId };
            h.Process = Start("worker", args + " " + spec.CommonArgs, spec.WorkerId, out string error);
            if (h.Process == null) { h.State = WorkerHandleState.Failed; h.Reason = error; }
            else h.State = WorkerHandleState.Running;
            _handles.Add(h);
            return h;
        }

        /// <summary>Start a non-worker role (the gateway) next to the orchestrator. Returns null on failure.</summary>
        public Process LaunchService(string role, string roleArgs, string logName, string commonArgs)
        {
            return Start(role, $"-nebula-role {role} {roleArgs} {commonArgs}", logName, out _);
        }

        private Process Start(string kind, string roleArgs, string logName, out string error)
        {
            error = "";
            string exe = ResolveExecutable();
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                error = $"executable not found ('{exe}'). Build the player and set NebulaConfig.WorkerExecutable (or -nebula-worker-exe).";
                _log("error", $"cannot launch {kind}: {error}");
                return null;
            }
            string logDir = Path.Combine(Path.GetDirectoryName(exe) ?? ".", "Logs");
            Directory.CreateDirectory(logDir);
            string args = $"-batchmode -nographics {roleArgs} -logFile \"{Path.Combine(logDir, logName + ".log")}\"";
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
            };
            try
            {
                var p = Process.Start(psi);
                _log("info", $"launched {kind} {logName} pid={p?.Id}");
                NebulaLog.Debugf($"{Path.GetFileName(exe)} {args}");
                return p;
            }
            catch (Exception e)
            {
                error = e.Message;
                _log("error", $"failed to launch {kind}: {e.Message}");
                return null;
            }
        }

        private string ResolveExecutable()
        {
            if (!string.IsNullOrEmpty(_executable)) return Path.GetFullPath(_executable);
            if (UnityEngine.Application.isEditor) return "";
            try { return Process.GetCurrentProcess().MainModule?.FileName; }
            catch { return ""; }
        }

        public void Kill(IWorkerHandle handle)
        {
            if (handle is Handle h)
            {
                KillProcess(h.Process);
                if (h.State != WorkerHandleState.Failed) { h.State = WorkerHandleState.Exited; h.Reason = "killed"; }
                _handles.Remove(h);
            }
        }

        public static void KillProcess(Process p)
        {
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        public static int SafePid(Process p)
        {
            try { return p != null ? p.Id : 0; } catch { return 0; }
        }

        public static bool HasExited(Process p)
        {
            try { return p == null || p.HasExited; } catch { return true; }
        }

        public void Tick()
        {
            for (int i = _handles.Count - 1; i >= 0; i--)
            {
                var h = _handles[i];
                if (h.State == WorkerHandleState.Running && HasExited(h.Process))
                {
                    int code = 0;
                    try { code = h.Process.ExitCode; } catch { }
                    h.State = WorkerHandleState.Exited;
                    h.Reason = $"exit code {code}";
                    _handles.RemoveAt(i);
                }
            }
        }

        public void WriteHandleJson(IWorkerHandle handle, JsonWriter w)
        {
            w.Prop("pid", handle is Handle h ? SafePid(h.Process) : 0);
        }

        public void Dispose()
        {
            foreach (var h in _handles) KillProcess(h.Process);
            _handles.Clear();
        }
    }
}
