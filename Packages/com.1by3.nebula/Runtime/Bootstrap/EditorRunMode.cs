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

        private EditorRunPlan(EditorPlayer player, string warning)
        {
            Player = player;
            Warning = warning;
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
            if (HasTag(tags, ServerTag)) return new EditorRunPlan(EditorPlayer.Server, null);
            if (HasTag(tags, ClientTag)) return new EditorRunPlan(EditorPlayer.Client, null);
            return new EditorRunPlan(isMainEditor ? EditorPlayer.Client : EditorPlayer.Server, null);
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
        public void ApplyTo(NebulaConfig config, Func<string, bool> hasArg)
        {
            if (Player == EditorPlayer.Mesh) return;
            if (!hasArg("nebula-gateway")) config.GatewayAddress = "127.0.0.1";
            if (Player != EditorPlayer.Server) return;
            // The whole mesh in this process: a control plane in memory, the one worker that is this process, and no
            // processes launched or retired around it.
            if (!hasArg("nebula-local-control-plane")) config.UseLocalControlPlane = true;
            if (!hasArg("nebula-database")) config.DatabaseUrl = "memory";
            if (!hasArg("nebula-workers")) config.WorkerCount = 1;
            if (!hasArg("nebula-min-workers")) config.MinWorkers = 1;
            if (!hasArg("nebula-max-workers")) config.MaxWorkers = 1;
            if (!hasArg("nebula-autoscale")) config.AutoScale = false;
        }

        public override string ToString() => Player == EditorPlayer.Client ? "Multiplayer Play Mode (this Editor is a client of the server a virtual player hosts)"
            : Player == EditorPlayer.Server ? "Multiplayer Play Mode (this virtual player hosts the server)"
            : "Mesh";
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
