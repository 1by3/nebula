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
    /// Developer loop: start SpacetimeDB, publish the control-plane module, launch a local mesh (orchestrator +
    /// gateway + N workers, all from the last build), then press Play in the Editor as a client.
    /// </summary>
    public static class NebulaMesh
    {
        private const string SpacetimeProcessKey = "Nebula.SpacetimePid";
        private const string OrchestratorProcessKey = "Nebula.OrchestratorPid";

        public static string ModuleDir => Path.Combine(Application.dataPath, "Nebula", "SpacetimeDB", "Module~");

        [MenuItem("Nebula/Control Plane/Start SpacetimeDB (local)", priority = 20)]
        public static void StartSpacetime()
        {
            if (IsAlive(SpacetimeProcessKey))
            {
                Debug.Log("[nebula] SpacetimeDB already running");
                return;
            }
            if (WaitForSpacetime("http://127.0.0.1:3000", 1))
            {
                Debug.Log("[nebula] SpacetimeDB already reachable on http://127.0.0.1:3000");
                return;
            }
            string dataDir = Path.Combine(NebulaBuild.ProjectRoot, "Temp", "spacetimedb");
            Directory.CreateDirectory(dataDir);
            var p = Run("spacetime", $"start --data-dir \"{dataDir}\" --listen-addr 127.0.0.1:3000", NebulaBuild.ProjectRoot, detached: true);
            if (p != null)
            {
                EditorPrefs.SetInt(SpacetimeProcessKey, p.Id);
                Debug.Log($"[nebula] SpacetimeDB started (pid {p.Id}) on http://127.0.0.1:3000");
            }
        }

        [MenuItem("Nebula/Control Plane/Publish Module", priority = 21)]
        public static void PublishModule()
        {
            var cfg = NebulaConfig.Load();
            if (!WaitForSpacetime(cfg.SpacetimeUri, 15))
            {
                Debug.LogError($"[nebula] SpacetimeDB is not reachable at {cfg.SpacetimeUri}; start it first (Nebula > Control Plane > Start SpacetimeDB)");
                return;
            }
            // --delete-data: the control plane holds only ephemeral registry state.
            RunAndLog("spacetime", $"publish -s local {cfg.SpacetimeDatabase} --delete-data -y", ModuleDir);
        }

        private static bool WaitForSpacetime(string uri, int seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var req = System.Net.WebRequest.CreateHttp(uri.TrimEnd('/') + "/v1/ping");
                    req.Timeout = 1000;
                    using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                    {
                        if ((int)resp.StatusCode < 500) return true;
                    }
                }
                catch { }
                System.Threading.Thread.Sleep(500);
            }
            return false;
        }

        [MenuItem("Nebula/Control Plane/Regenerate C# Bindings", priority = 22)]
        public static void RegenerateBindings()
        {
            var outDir = Path.Combine(Application.dataPath, "Nebula", "SpacetimeDB", "Generated");
            RunAndLog("spacetime", $"generate --lang csharp --module-path . --out-dir \"{outDir}\" --namespace Nebula.Spacetime -y", ModuleDir);
            AssetDatabase.Refresh();
        }

        [MenuItem("Nebula/Mesh/Start Local Mesh (orchestrator + gateway + workers)", priority = 40)]
        public static void StartLocalMesh()
        {
            if (!File.Exists(NebulaBuild.ExecutablePath))
            {
                Debug.LogError($"[nebula] no build at {NebulaBuild.ExecutablePath}. Run Nebula > Build first.");
                return;
            }
            if (IsAlive(OrchestratorProcessKey))
            {
                Debug.LogWarning("[nebula] a local mesh is already running; stop it first");
                return;
            }
            var cfg = NebulaConfig.Load();
            StartSpacetime();
            PublishModule();
            string logs = Path.Combine(NebulaBuild.BuildDir, "Logs");
            Directory.CreateDirectory(logs);
            string args = $"-batchmode -nographics -nebula-role orchestrator -nebula-workers {cfg.WorkerCount} -nebula-spacetime {cfg.SpacetimeUri} -nebula-database {cfg.SpacetimeDatabase} -logFile \"{Path.Combine(logs, "orchestrator.log")}\"";
            var p = Run(NebulaBuild.ExecutablePath, args, NebulaBuild.BuildDir, detached: true);
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
            foreach (var p in Process.GetProcessesByName("Nebula"))
            {
                try { p.Kill(); } catch { }
            }
            Debug.Log("[nebula] local mesh stopped");
        }

        [MenuItem("Nebula/Mesh/Open Dashboard (workers, containers, players)", priority = 43)]
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

        [MenuItem("Nebula/Control Plane/Stop SpacetimeDB", priority = 23)]
        public static void StopSpacetime()
        {
            KillTracked(SpacetimeProcessKey);
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
