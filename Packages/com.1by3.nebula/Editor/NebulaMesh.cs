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

        /// <summary>The control-plane module sources inside the Nebula package (wherever Unity resolved it).</summary>
        public static string ModuleDir => PackagePath("SpacetimeDB", "Module~");
        /// <summary>The persistence module sources inside the Nebula package.</summary>
        public static string PersistenceModuleDir => PackagePath("SpacetimeDB", "PersistenceModule~");
        /// <summary>Generated client bindings for the control-plane module.</summary>
        private static string GeneratedDir => PackagePath("SpacetimeDB", "Generated");
        /// <summary>Generated client bindings for the persistence module.</summary>
        private static string GeneratedPersistenceDir => PackagePath("SpacetimeDB", "GeneratedPersistence");

        /// <summary>Absolute path of a folder inside the com.1by3.nebula package, embedded or resolved elsewhere.</summary>
        private static string PackagePath(params string[] parts)
        {
            string rel = Path.Combine("Packages", PackageName);
            foreach (var part in parts) rel = Path.Combine(rel, part);
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + PackageName + "/package.json");
            if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
            {
                string p = info.resolvedPath;
                foreach (var part in parts) p = Path.Combine(p, part);
                return p;
            }
            // Embedded package: "Packages/..." is a real folder next to Assets.
            return Path.GetFullPath(rel);
        }

        private const string PackageName = "com.1by3.nebula";

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

        [MenuItem("Nebula/Control Plane/Publish Modules (control plane + persistence)", priority = 21)]
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
            // Persistence is a database of its own and keeps the saved entities: never --delete-data here.
            RunAndLog("spacetime", $"publish -s local {cfg.PersistenceDatabase} -y", PersistenceModuleDir);
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

        [MenuItem("Nebula/Control Plane/Regenerate C# Bindings (both modules)", priority = 22)]
        public static void RegenerateBindings()
        {
            RunAndLog("spacetime", $"generate --lang csharp --module-path . --out-dir \"{GeneratedDir}\" --namespace Nebula.Spacetime -y", ModuleDir);
            RunAndLog("spacetime", $"generate --lang csharp --module-path . --out-dir \"{GeneratedPersistenceDir}\" --namespace Nebula.Spacetime.Persistence -y", PersistenceModuleDir);
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
            StartSpacetime();
            PublishModule();
            string logs = Path.Combine(NebulaBuild.BuildDir, "Logs");
            Directory.CreateDirectory(logs);
            string args = $"-nebula-worker-exe \"{NebulaBuild.ExecutablePath}\" -nebula-workers {cfg.WorkerCount} -nebula-spacetime {cfg.SpacetimeUri} -nebula-database {cfg.SpacetimeDatabase} -nebula-persistence {cfg.SpacetimeUri} -nebula-persistence-database {cfg.PersistenceDatabase} -logFile \"{Path.Combine(logs, "orchestrator.log")}\"";
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
