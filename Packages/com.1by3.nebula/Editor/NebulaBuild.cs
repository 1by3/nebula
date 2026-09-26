using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Nebula.Editor
{
    /// <summary>
    /// Builds the Unity player for workers and clients, exports service data, and publishes the standalone .NET
    /// orchestrator and gateway beside it. Requires the .NET 10 SDK. Menu: Nebula > Build.
    /// <list type="bullet">
    /// <item>Windows player: <c>Builds/Win64/Nebula.exe</c> - the local mesh, bots and the human client.</item>
    /// <item>Linux player: <c>Builds/Linux64Player/Nebula.x86_64</c> - the human client on Linux desktops.</item>
    /// <item>Linux dedicated server: <c>Builds/Linux64/Nebula.x86_64</c> - the worker simulation on cloud VMs.</item>
    /// </list>
    /// Batchmode: <c>Unity -batchmode -quit -projectPath . -executeMethod Nebula.Editor.NebulaBuild.BuildWindowsBatch</c>,
    /// <c>...NebulaBuild.BuildLinuxBatch</c>, <c>...NebulaBuild.BuildMacBatch</c>, <c>...NebulaBuild.BuildLinuxServerBatch</c>
    /// or <c>...NebulaBuild.BuildWebBatch</c>.
    /// </summary>
    public static class NebulaBuild
    {
        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        public static string BuildDir => Path.Combine(ProjectRoot, "Builds", "Win64");
        public static string ExecutablePath => Path.Combine(BuildDir, "Nebula.exe");
        public static string LinuxBuildDir => Path.Combine(ProjectRoot, "Builds", "Linux64");
        public static string LinuxExecutablePath => Path.Combine(LinuxBuildDir, "Nebula.x86_64");
        public static string LinuxPlayerBuildDir => Path.Combine(ProjectRoot, "Builds", "Linux64Player");
        public static string LinuxPlayerExecutablePath => Path.Combine(LinuxPlayerBuildDir, "Nebula.x86_64");

        [MenuItem("Nebula/Build/Windows Player (Mono, development)", priority = 0)]
        public static void BuildWindowsMenu()
        {
            var report = BuildWindows();
            if (report != null && report.summary.result == BuildResult.Succeeded)
            {
                EditorUtility.RevealInFinder(ExecutablePath);
            }
        }

        [MenuItem("Nebula/Build/Linux Player (Mono, development)", priority = 1)]
        public static void BuildLinuxMenu()
        {
            var report = BuildLinux();
            if (report != null && report.summary.result == BuildResult.Succeeded)
            {
                EditorUtility.RevealInFinder(LinuxPlayerExecutablePath);
            }
        }

        [MenuItem("Nebula/Build/Linux Dedicated Server (Mono, development)", priority = 2)]
        public static void BuildLinuxServerMenu()
        {
            var report = BuildLinuxServer();
            if (report != null && report.summary.result == BuildResult.Succeeded)
            {
                EditorUtility.RevealInFinder(LinuxExecutablePath);
            }
        }

        public static BuildReport BuildWindows()
        {
            return Build(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, ExecutablePath, NamedBuildTarget.Standalone);
        }

        /// <summary>
        /// Linux player (Builds/Linux64Player/Nebula.x86_64): the client for Linux desktops. It has its own folder so it
        /// never overwrites the dedicated server in Builds/Linux64.
        /// </summary>
        public static BuildReport BuildLinux()
        {
            return Build(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Player, LinuxPlayerExecutablePath, NamedBuildTarget.Standalone);
        }

        public static BuildReport BuildLinuxServer()
        {
            return Build(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, LinuxExecutablePath, NamedBuildTarget.Server);
        }

        /// <summary>macOS player (Builds/MacOS/Nebula.app): what `nebula start` runs on a Mac.</summary>
        public static BuildReport BuildMac()
        {
            return Build(BuildTarget.StandaloneOSX, StandaloneBuildSubtarget.Player, MacExecutablePath, NamedBuildTarget.Standalone);
        }

        public static string MacBuildDir => Path.Combine(ProjectRoot, "Builds", "MacOS");
        public static string MacExecutablePath => Path.Combine(MacBuildDir, "Nebula.app");
        public static string WebBuildDir => Path.Combine(ProjectRoot, "Builds", "Web");

        [MenuItem("Nebula/Build/Web Client", priority = 3)]
        public static void BuildWebMenu()
        {
            var report = BuildWeb();
            if (report != null && report.summary.result == BuildResult.Succeeded)
            {
                EditorUtility.RevealInFinder(Path.Combine(WebBuildDir, "index.html"));
            }
        }

        /// <summary>
        /// Web client (Builds/Web): a browser build that reaches the gateway over WebRTC. The standalone gateway serves
        /// it from a Web folder beside its own build folder, so `nebula start` puts it at http://127.0.0.1:7000/.
        /// The build is gzip-compressed, because browsers accept brotli only over HTTPS.
        /// </summary>
        public static BuildReport BuildWeb()
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0)
            {
                Debug.LogError("[nebula] no scenes in Build Settings. Add your game scene in File > Build Profiles.");
                return null;
            }
            var compression = PlayerSettings.WebGL.compressionFormat;
            var fallback = PlayerSettings.WebGL.decompressionFallback;
            bool runInBackground = PlayerSettings.runInBackground;
            try
            {
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
                PlayerSettings.WebGL.decompressionFallback = false;
                PlayerSettings.runInBackground = true;
                var options = new BuildPlayerOptions { scenes = scenes, locationPathName = WebBuildDir, target = BuildTarget.WebGL, options = BuildOptions.None };
                Debug.Log($"[nebula] building {WebBuildDir} (WebGL) with scenes: {string.Join(", ", scenes)}");
                var report = BuildPipeline.BuildPlayer(options);
                Debug.Log($"[nebula] build {report.summary.result} in {report.summary.totalTime.TotalSeconds:F0}s, {report.summary.totalErrors} errors");
                return report;
            }
            finally
            {
                PlayerSettings.WebGL.compressionFormat = compression;
                PlayerSettings.WebGL.decompressionFallback = fallback;
                PlayerSettings.runInBackground = runInBackground;
            }
        }

        /// <summary>Entry point for batchmode web client builds; exits with a non-zero code on failure.</summary>
        public static void BuildWebBatch() => ExitOnFailure(BuildWeb());

        private static BuildReport Build(BuildTarget target, StandaloneBuildSubtarget subtarget, string location, NamedBuildTarget named)
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0)
            {
                Debug.LogError("[nebula] no scenes in Build Settings. Add your game scene in File > Build Profiles.");
                return null;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(location));
            PlayerSettings.SetScriptingBackend(named, ScriptingImplementation.Mono2x);
            PlayerSettings.runInBackground = true;
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = location,
                target = target,
                subtarget = (int)subtarget,
                // CleanBuildCache: every script assembly is compiled from the current sources. The incremental pipeline
                // has reused a game assembly compiled a day earlier while the package assembly was fresh (a build from a
                // mirrored project), which the worker only reveals at run time as a MissingFieldException. A deploy
                // build is worth the extra minute.
                options = BuildOptions.Development | BuildOptions.CleanBuildCache,
            };
            Debug.Log($"[nebula] building {location} ({target}/{subtarget}) with scenes: {string.Join(", ", scenes)}");
            ServiceExport.Write(Path.GetDirectoryName(location), scenes);
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[nebula] build {report.summary.result} in {report.summary.totalTime.TotalSeconds:F0}s, {report.summary.totalErrors} errors");
            if (report.summary.result == BuildResult.Succeeded && !CommandLine.Has("nebula-skip-service-publish"))
                ServiceExport.Publish(Path.GetDirectoryName(location), target);
            return report;
        }

        /// <summary>Entry point for batchmode builds; exits with a non-zero code on failure.</summary>
        public static void BuildWindowsBatch() => ExitOnFailure(BuildWindows());

        /// <summary>Entry point for batchmode Linux player builds; exits with a non-zero code on failure.</summary>
        public static void BuildLinuxBatch() => ExitOnFailure(BuildLinux());

        /// <summary>Entry point for batchmode Linux server builds; exits with a non-zero code on failure.</summary>
        public static void BuildLinuxServerBatch() => ExitOnFailure(BuildLinuxServer());

        /// <summary>Entry point for batchmode macOS player builds; exits with a non-zero code on failure.</summary>
        public static void BuildMacBatch() => ExitOnFailure(BuildMac());

        private static void ExitOnFailure(BuildReport report)
        {
            if (report == null || report.summary.result != BuildResult.Succeeded)
            {
                EditorApplication.Exit(1);
            }
        }
    }
}
