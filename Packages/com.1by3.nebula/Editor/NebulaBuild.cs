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
    /// Builds the executables every Nebula role runs from. Workers and services run them with
    /// <c>-batchmode -nographics -nebula-role ...</c>; players run the Windows build as-is. Menu: Nebula > Build.
    /// <list type="bullet">
    /// <item>Windows player: <c>Builds/Win64/Nebula.exe</c> - the local mesh, bots and the human client.</item>
    /// <item>Linux dedicated server: <c>Builds/Linux64/Nebula.x86_64</c> - what runs on cloud VMs (orchestrator,
    /// gateway, workers). No graphics subsystem at all, so it needs no display libraries on the machine.</item>
    /// </list>
    /// Batchmode: <c>Unity -batchmode -quit -projectPath . -executeMethod Nebula.Editor.NebulaBuild.BuildWindowsBatch</c>
    /// or <c>...NebulaBuild.BuildLinuxServerBatch</c>.
    /// </summary>
    public static class NebulaBuild
    {
        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        public static string BuildDir => Path.Combine(ProjectRoot, "Builds", "Win64");
        public static string ExecutablePath => Path.Combine(BuildDir, "Nebula.exe");
        public static string LinuxBuildDir => Path.Combine(ProjectRoot, "Builds", "Linux64");
        public static string LinuxExecutablePath => Path.Combine(LinuxBuildDir, "Nebula.x86_64");

        [MenuItem("Nebula/Build/Windows Player (Mono, development)", priority = 0)]
        public static void BuildWindowsMenu()
        {
            var report = BuildWindows();
            if (report != null && report.summary.result == BuildResult.Succeeded)
            {
                EditorUtility.RevealInFinder(ExecutablePath);
            }
        }

        [MenuItem("Nebula/Build/Linux Dedicated Server (Mono, development)", priority = 1)]
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
                options = BuildOptions.Development,
            };
            Debug.Log($"[nebula] building {location} ({target}/{subtarget}) with scenes: {string.Join(", ", scenes)}");
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"[nebula] build {report.summary.result} in {report.summary.totalTime.TotalSeconds:F0}s, {report.summary.totalErrors} errors");
            return report;
        }

        /// <summary>Entry point for batchmode builds; exits with a non-zero code on failure.</summary>
        public static void BuildWindowsBatch() => ExitOnFailure(BuildWindows());

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
