using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Nebula.Editor
{
    /// <summary>
    /// Developer loop: launch a local mesh from the last build (the standalone orchestrator, which hosts the control
    /// plane and starts the gateway and N workers), then press Play in the Editor as a client. The CLI
    /// (<c>nebula start</c>) does the same with more checks; these menu items are the in-Editor shortcut.
    /// </summary>
    public static class NebulaMesh
    {
        private const string OrchestratorProcessKey = "Nebula.OrchestratorPid";

        [MenuItem("Nebula/Mesh/Start Local Mesh (orchestrator + gateway + workers)", priority = 40)]
        public static void StartLocalMesh()
        {
            if (!File.Exists(NebulaBuild.ExecutablePath))
            {
                Debug.LogError($"[nebula] no build at {NebulaBuild.ExecutablePath}. Run Nebula > Build first.");
                return;
            }
            string orchestrator = Path.Combine(NebulaBuild.BuildDir, "nebula-orchestrator.exe");
            if (!File.Exists(orchestrator) || !File.Exists(Path.Combine(NebulaBuild.BuildDir, "nebula-gateway.exe")) || !File.Exists(Path.Combine(NebulaBuild.BuildDir, "nebula-services.json")))
            {
                Debug.LogError("[nebula] standalone services are missing. Run Nebula > Build or nebula build first.");
                return;
            }
            if (IsAlive(OrchestratorProcessKey))
            {
                Debug.LogWarning("[nebula] a local mesh is already running; stop it first");
                return;
            }
            var cfg = NebulaConfig.Load();
            string logs = Path.Combine(NebulaBuild.BuildDir, "Logs");
            Directory.CreateDirectory(logs);
            // The orchestrator hosts the control plane and keeps it, with every saved entity, in a SQLite file
            // under Library so it survives rebuilds (NebulaConfig.DatabaseUrl, when set, wins).
            string database = string.IsNullOrEmpty(cfg.DatabaseUrl) ? "sqlite:" + Path.Combine(NebulaBuild.ProjectRoot, "Library", "Nebula", "nebula.db") : cfg.DatabaseUrl;
            string args = $"-nebula-worker-exe \"{NebulaBuild.ExecutablePath}\" -nebula-workers {cfg.WorkerCount} -nebula-database \"{database}\" -logFile \"{Path.Combine(logs, "orchestrator.log")}\"";
            var p = Run(orchestrator, args, NebulaBuild.BuildDir, detached: true);
            if (p != null)
            {
                EditorPrefs.SetInt(OrchestratorProcessKey, p.Id);
                Debug.Log($"[nebula] local mesh starting (orchestrator pid {p.Id}); logs in {logs}. Dashboard: http://localhost:{cfg.DashboardPort}/ (Nebula > Mesh > Open Dashboard). Press Play to join as a client.");
            }
        }

        [MenuItem("Nebula/Mesh/Stop Local Mesh", priority = 41)]
        public static void StopLocalMesh()
        {
            KillTracked(OrchestratorProcessKey);
            // The orchestrator kills its children on exit, but a hard kill skips that: sweep any stragglers.
            foreach (string name in new[] { "Nebula", "nebula-orchestrator", "nebula-gateway" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (string.Equals(Path.GetDirectoryName(p.MainModule.FileName), NebulaBuild.BuildDir, StringComparison.OrdinalIgnoreCase)) p.Kill();
                }
                catch { }
                finally { p.Dispose(); }
            }
            Debug.Log("[nebula] local mesh stopped");
        }

        [MenuItem("Nebula/Mesh/Open Nebula Dashboard (workers, containers, players)", priority = 43)]
        public static void OpenDashboard()
        {
            var cfg = NebulaConfig.Load();
            Application.OpenURL($"http://localhost:{cfg.DashboardPort}/");
        }

        [MenuItem("Nebula/Mesh/Launch Extra Client Window", priority = 42)]
        public static void LaunchClient()
        {
            if (!File.Exists(NebulaBuild.ExecutablePath))
            {
                Debug.LogError($"[nebula] no build at {NebulaBuild.ExecutablePath}");
                return;
            }
            string logs = Path.Combine(NebulaBuild.BuildDir, "Logs");
            Directory.CreateDirectory(logs);
            int n = UnityEngine.Random.Range(100, 999);
            Run(NebulaBuild.ExecutablePath, $"-nebula-role client -nebula-name client{n} -screen-width 1280 -screen-height 720 -screen-fullscreen 0 -logFile \"{Path.Combine(logs, $"client{n}.log")}\"", NebulaBuild.BuildDir, detached: true);
        }

        private static bool IsAlive(string key)
        {
            int pid = EditorPrefs.GetInt(key, 0);
            if (pid == 0) return false;
            try { return !Process.GetProcessById(pid).HasExited; } catch { return false; }
        }

        private static void KillTracked(string key)
        {
            int pid = EditorPrefs.GetInt(key, 0);
            if (pid != 0)
            {
                try { Process.GetProcessById(pid).Kill(); } catch { }
                EditorPrefs.DeleteKey(key);
            }
        }

        private static Process Run(string file, string args, string workingDir, bool detached)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    WorkingDirectory = workingDir,
                    UseShellExecute = detached,
                    CreateNoWindow = !detached,
                };
                return Process.Start(psi);
            }
            catch (Exception e)
            {
                Debug.LogError($"[nebula] failed to run '{file} {args}': {e.Message}");
                return null;
            }
        }

        private static void RunAndLog(string file, string args, string workingDir)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    Debug.Log($"[nebula] {file} {args}\n{stdout}\n{stderr}\nexit {p.ExitCode}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[nebula] failed to run '{file} {args}': {e.Message}");
            }
        }
    }
}
