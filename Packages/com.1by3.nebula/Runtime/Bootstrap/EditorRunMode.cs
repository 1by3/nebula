using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nebula
{
    /// <summary>What pressing Play in the Editor starts (<see cref="NebulaConfig.EditorRunMode"/>). Builds are never affected.</summary>
    public enum NebulaEditorRunMode
    {
        /// <summary>
        /// The Editor runs <see cref="NebulaBootstrap.EditorRole"/> and joins a mesh started from a build with
        /// <c>nebula start</c>. The default, and the only mode that tests more than one worker.
        /// </summary>
        Mesh = 0,
        /// <summary>
        /// No build: a Multiplayer Play Mode virtual player hosts the whole server in its own Editor process
        /// (orchestrator, gateway and one worker, with the control plane in memory), and the main Editor is a client
        /// that connects to it. Needs Unity's Multiplayer Play Mode package and one enabled virtual player.
        /// </summary>
        MultiplayerPlayMode = 1,
    }

    /// <summary>Where the server a virtual player hosts keeps persistent entities (<see cref="NebulaConfig.EditorPersistence"/>).</summary>
    public enum NebulaEditorPersistence
    {
        /// <summary>A save file inside the project's <c>Library/Nebula/DevSaves</c> folder, kept from one Play to the next. <c>Nebula &gt; Dev Loop &gt; Reset Dev Saves</c> deletes it.</summary>
        DevSaveFile = 0,
        /// <summary>Nothing is kept: every Play starts from a fresh world.</summary>
        InMemory = 1,
    }

    /// <summary>
    /// What one Editor process does under <see cref="NebulaEditorRunMode.MultiplayerPlayMode"/>, decided by
    /// <see cref="EditorRunPlan.Resolve"/> from facts the bootstrap reads (whether this is the Editor, whether this is
    /// the main Editor or a virtual player, its Multiplayer Play Mode tags, and the command line), so the rule can be
    /// tested without Multiplayer Play Mode.
    /// </summary>
    public enum EditorPlayer
    {
        /// <summary>The run mode does not apply: a build, <see cref="NebulaEditorRunMode.Mesh"/>, or an explicit <c>-nebula-role</c>.</summary>
        Mesh,
        /// <summary>A client that connects to the server a virtual player hosts.</summary>
        Client,
        /// <summary>The virtual player that hosts the server: orchestrator, gateway and one worker in one process.</summary>
        Server,
    }

    /// <summary>The decision for one Editor process in the Multiplayer Play Mode dev loop, and the settings it implies.</summary>
    public readonly struct EditorRunPlan
    {
        /// <summary>A virtual player with this tag (any case) hosts the server; one with <see cref="ClientTag"/> is a client. Untagged, the main Editor is the client and a virtual player is the server.</summary>
        public const string ServerTag = "Server";
        /// <summary>A player with this tag is a client, so a second virtual player can be a second player.</summary>
        public const string ClientTag = "Client";

        public EditorPlayer Player { get; }
        /// <summary>The roles this process runs; <see cref="NebulaRoles.None"/> for <see cref="EditorPlayer.Mesh"/>, which keeps the usual rule.</summary>
        public NebulaRoles Roles => Player == EditorPlayer.Client ? NebulaRoles.Client
            : Player == EditorPlayer.Server ? NebulaRoles.Worker | NebulaRoles.Gateway | NebulaRoles.Orchestrator
            : NebulaRoles.None;
        /// <summary>Why the plan is <see cref="EditorPlayer.Mesh"/> when the mode asked for more; null otherwise.</summary>
        public string Warning { get; }
        /// <summary>
        /// Whether this Editor closes its Game view while it plays: true only for the server when a virtual player
        /// hosts it, never for the main Editor or a client. The Game view builds the render pipeline each time it
        /// draws, whatever the cameras do, and a virtual player that cannot build the pipeline logs errors every frame.
        /// The Editor puts a <c>Nebula Server</c> tab in the Game view's place.
        /// </summary>
        public bool HidesGameView { get; }

        private EditorRunPlan(EditorPlayer player, string warning, bool hidesGameView = false)
        {
            Player = player;
            Warning = warning;
            HidesGameView = hidesGameView;
        }

        /// <param name="mode">The configured <see cref="NebulaConfig.EditorRunMode"/>.</param>
        /// <param name="isEditor">Whether this process is the Unity Editor. A build is always <see cref="EditorPlayer.Mesh"/>.</param>
        /// <param name="playModeAvailable">Whether Multiplayer Play Mode's <c>CurrentPlayer</c> could be read.</param>
        /// <param name="isMainEditor">Whether this is the main Editor rather than a virtual player.</param>
        /// <param name="tags">This player's Multiplayer Play Mode tags; null or empty for none.</param>
        /// <param name="explicitRole">Whether <c>-nebula-role</c> was given, which always wins.</param>
        public static EditorRunPlan Resolve(NebulaEditorRunMode mode, bool isEditor, bool playModeAvailable, bool isMainEditor, IReadOnlyList<string> tags, bool explicitRole)
        {
            if (mode != NebulaEditorRunMode.MultiplayerPlayMode || !isEditor || explicitRole) return new EditorRunPlan(EditorPlayer.Mesh, null);
            if (!playModeAvailable)
                return new EditorRunPlan(EditorPlayer.Mesh, "EditorRunMode is MultiplayerPlayMode, but Multiplayer Play Mode is not available in this Editor (it needs Unity 6 with the com.unity.multiplayer.playmode package); running as EditorRunMode Mesh");
            if (HasTag(tags, ServerTag)) return new EditorRunPlan(EditorPlayer.Server, null, hidesGameView: !isMainEditor);
            if (HasTag(tags, ClientTag)) return new EditorRunPlan(EditorPlayer.Client, null);
            return isMainEditor ? new EditorRunPlan(EditorPlayer.Client, null) : new EditorRunPlan(EditorPlayer.Server, null, hidesGameView: true);
        }

        private static bool HasTag(IReadOnlyList<string> tags, string tag)
        {
            if (tags == null) return false;
            for (int i = 0; i < tags.Count; i++)
                if (string.Equals(tags[i]?.Trim(), tag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Apply the plan's settings to <paramref name="config"/> (the bootstrap's copy, never the asset). A setting
        /// whose command-line switch was given (<paramref name="hasArg"/>, without the leading dash) keeps the
        /// command line's value. Does nothing for <see cref="EditorPlayer.Mesh"/>.
        /// </summary>
        /// <param name="projectRoot">The project's root folder, from <see cref="EditorDevPaths.ProjectRoot"/>: the dev loop's files live under its <c>Library/Nebula</c>.</param>
        public void ApplyTo(NebulaConfig config, Func<string, bool> hasArg, string projectRoot)
        {
            if (Player == EditorPlayer.Mesh) return;
            // Both sides move off the usual ports by the same offset, so a mesh started with nebula start can run too.
            int offset = config.EditorPortOffset;
            if (!hasArg("nebula-gateway"))
            {
                config.GatewayAddress = "127.0.0.1";
                config.GatewayPort = Offset(config.GatewayPort, offset);
            }
            if (Player != EditorPlayer.Server) return;
            config.WorkerBasePort = Offset(config.WorkerBasePort, offset);
            if (!hasArg("nebula-dashboard-port") && config.DashboardPort != 0) config.DashboardPort = Offset(config.DashboardPort, offset);
            // The whole mesh in this process: a control plane in memory, the one worker that is this process, and no
            // processes launched or retired around it.
            if (!hasArg("nebula-local-control-plane")) config.UseLocalControlPlane = true;
            if (!hasArg("nebula-database")) config.DatabaseUrl = "memory";
            if (!hasArg("nebula-workers")) config.WorkerCount = 1;
            if (!hasArg("nebula-min-workers")) config.MinWorkers = 1;
            if (!hasArg("nebula-max-workers")) config.MaxWorkers = 1;
            if (!hasArg("nebula-autoscale")) config.AutoScale = false;
            // Saved entities: a file of the project's own, never the one a mesh started with nebula start uses, or nothing.
            if (!hasArg("nebula-persistence-mode")) config.PersistenceMode = config.EditorPersistence == NebulaEditorPersistence.InMemory ? "memory" : "local";
            if (!hasArg("nebula-persistence-file") && config.EditorPersistence == NebulaEditorPersistence.DevSaveFile)
                config.PersistenceLocalFile = EditorDevPaths.DevSaveFile(projectRoot);
        }

        /// <summary>A port moved by <paramref name="offset"/>, or left alone when that would leave the valid range.</summary>
        private static ushort Offset(ushort port, int offset)
        {
            int moved = port + offset;
            return moved >= 1 && moved <= 65535 ? (ushort)moved : port;
        }

        public override string ToString() => Player == EditorPlayer.Client ? "Multiplayer Play Mode (this Editor is a client of the server a virtual player hosts)"
            : Player == EditorPlayer.Server ? "Multiplayer Play Mode (this virtual player hosts the server)"
            : "Mesh";
    }

    /// <summary>
    /// Where the Multiplayer Play Mode dev loop keeps its files: under the project's <c>Library/Nebula</c>, shared by
    /// the main Editor and every virtual player, and never read by a build.
    /// </summary>
    public static class EditorDevPaths
    {
        /// <summary>
        /// The project's root folder from <see cref="Application.dataPath"/> (<c>&lt;root&gt;/Assets</c>). A virtual
        /// player runs from a copy of the project inside <c>&lt;root&gt;/Library/VP/&lt;player&gt;</c>, so its own
        /// data path is walked back out of that folder to the project it belongs to.
        /// </summary>
        public static string ProjectRoot(string dataPath)
        {
            if (string.IsNullOrEmpty(dataPath)) return "";
            string[] parts = dataPath.Replace('\\', '/').TrimEnd('/').Split('/');
            // Drop the Assets folder, then step out of Library/VP/<player> when this is a virtual player's copy.
            int end = parts.Length - 1;
            for (int i = end - 2; i >= 1; i--)
            {
                if (string.Equals(parts[i], "VP", StringComparison.OrdinalIgnoreCase) && string.Equals(parts[i - 1], "Library", StringComparison.OrdinalIgnoreCase))
                {
                    end = i - 1;
                    break;
                }
            }
            return string.Join("/", parts, 0, end);
        }

        /// <summary>The dev loop's folder: <c>&lt;root&gt;/Library/Nebula</c>.</summary>
        public static string Folder(string projectRoot) => System.IO.Path.Combine(projectRoot, "Library", "Nebula");

        /// <summary>The save file of <see cref="NebulaEditorPersistence.DevSaveFile"/>: <c>Library/Nebula/DevSaves/world.bin</c>.</summary>
        public static string DevSaveFile(string projectRoot) => System.IO.Path.Combine(Folder(projectRoot), "DevSaves", "world.bin");

        /// <summary>
        /// The player signing key of the server a virtual player hosts (<see cref="NebulaGateway.AuthKeyPath"/>):
        /// <c>Library/Nebula/dev-auth.key</c>. A virtual player's persistent data folder is not the main Editor's, so
        /// without this the identity the main Editor saved would be refused once every session. Builds never read it.
        /// </summary>
        public static string DevAuthKey(string projectRoot) => System.IO.Path.Combine(Folder(projectRoot), "dev-auth.key");

        /// <summary>The folder <see cref="DevSaveFile"/> is in.</summary>
        public static string DevSaves(string projectRoot) => System.IO.Path.Combine(Folder(projectRoot), "DevSaves");
    }

    /// <summary>Reads the running Editor's Multiplayer Play Mode player, where the Editor has one.</summary>
    internal static class MultiplayerPlayModePlayer
    {
        /// <summary>Whether this is the main Editor and its tags; false when Multiplayer Play Mode is not available.</summary>
        public static bool TryRead(out bool isMainEditor, out string[] tags)
        {
            isMainEditor = true;
            tags = Array.Empty<string>();
#if UNITY_6000_6_OR_NEWER
            // Unity 6.6 has CurrentPlayer in the engine (UnityEngine.MultiplayerModule): no package reference needed.
            try
            {
                isMainEditor = Unity.Multiplayer.PlayMode.CurrentPlayer.IsMainEditor;
                tags = Unity.Multiplayer.PlayMode.CurrentPlayer.ReadOnlyTags() ?? Array.Empty<string>();
                return true;
            }
            catch (Exception)
            {
                // Without the package's players behind it (or outside Play) CurrentPlayer throws: not available.
                return false;
            }
#else
            // Earlier Editors have it in the com.unity.multiplayer.playmode package, which this package does not reference.
            return TryReadByReflection(out isMainEditor, out tags);
#endif
        }

#if !UNITY_6000_6_OR_NEWER
        private static bool TryReadByReflection(out bool isMainEditor, out string[] tags)
        {
            isMainEditor = true;
            tags = Array.Empty<string>();
            Type type = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType("Unity.Multiplayer.PlayMode.CurrentPlayer", false) ?? assembly.GetType("Unity.Multiplayer.Playmode.CurrentPlayer", false);
                if (type != null) break;
            }
            if (type == null) return false;
            try
            {
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
                var main = type.GetProperty("IsMainEditor", flags);
                var read = type.GetMethod("ReadOnlyTags", flags, null, Type.EmptyTypes, null);
                if (main == null || read == null) return false;
                isMainEditor = (bool)main.GetValue(null);
                tags = read.Invoke(null, null) as string[] ?? Array.Empty<string>();
                return true;
            }
            catch (Exception)
            {
                // Without the package's players behind it (or outside Play) CurrentPlayer throws: not available.
                return false;
            }
        }
#endif
    }

    /// <summary>
    /// Keeps a server that a virtual player hosts from rendering. It has nothing to show, and a virtual player of
    /// some Editor versions cannot compile the render pipeline's shaders, which fills its log and costs frames.
    /// Turning cameras off is not enough on its own: the Game view builds the render pipeline each time it draws,
    /// with or without cameras, so the Editor also closes the server's Game view (<see cref="EditorRunPlan.HidesGameView"/>,
    /// done by <c>Nebula.Editor.ServerPlayerView</c>). The pipeline assets are left alone on purpose: a virtual
    /// player shares the main Editor's ProjectSettings folder, and changing them there would be saved into it.
    /// </summary>
    internal static class HeadlessEditorPlayer
    {
        private static readonly List<Camera> Buffer = new List<Camera>();

        /// <summary>Turn off every enabled camera; called each frame, so cameras a scene or a script adds later go too.</summary>
        public static void DisableCameras()
        {
            if (Camera.allCamerasCount == 0) return;
            Buffer.Clear();
            Buffer.AddRange(Camera.allCameras);
            foreach (var camera in Buffer) camera.enabled = false;
            Buffer.Clear();
        }
    }
}
