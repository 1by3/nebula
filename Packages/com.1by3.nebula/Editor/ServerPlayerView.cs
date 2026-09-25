using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Nebula.Editor
{
    /// <summary>
    /// Takes the Game view's place in a Multiplayer Play Mode virtual player that hosts the server
    /// (<see cref="EditorRunPlan.HidesGameView"/>). The Game view builds the render pipeline each time it draws,
    /// whether or not a camera is on. A virtual player on Unity 6000.6 cannot build the Universal Render Pipeline, so
    /// a Game view left open there logs errors with stack traces every frame. When such a server enters Play, this tab
    /// opens next to the Game view, and the Game view closes. Unity may open a new Game view at the next Play; it
    /// closes again. When the same player later plays as a client, the Game view comes back and this tab closes.
    /// </summary>
    /// <remarks>
    /// Only windows of this Editor process change. Nothing touches the render pipeline assets, GraphicsSettings or
    /// QualitySettings: a virtual player's ProjectSettings folder is a link to the main project's, and a pipeline
    /// changed from a script is saved there when the Editor saves or quits.
    /// </remarks>
    [InitializeOnLoad]
    public sealed class ServerPlayerView : EditorWindow
    {
        private const string Title = "Nebula Server";
        /// <summary>Editor updates to wait, after entering Play, for <see cref="NebulaBootstrap"/> to resolve its plan.</summary>
        private const int MaxWaitUpdates = 600;

        private static int _waited;

        static ServerPlayerView()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredPlayMode) return;
            _waited = 0;
            EditorApplication.update -= ApplyPlan;
            EditorApplication.update += ApplyPlan;
        }

        /// <summary>Once the bootstrap has resolved this process's plan, hide or restore the Game view to match.</summary>
        private static void ApplyPlan()
        {
            if (!EditorApplication.isPlaying)
            {
                EditorApplication.update -= ApplyPlan;
                return;
            }
            var bootstrap = NebulaBootstrap.Instance;
            if (bootstrap == null)
            {
                // No bootstrap yet (or a scene without Nebula): keep waiting a little, then leave the windows as they are.
                if (++_waited >= MaxWaitUpdates) EditorApplication.update -= ApplyPlan;
                return;
            }
            EditorApplication.update -= ApplyPlan;
            if (bootstrap.RunPlan.HidesGameView) HideGameViews();
            else RestoreGameView();
        }

        /// <summary>The Game view's base class (internal to Unity), or the Game view itself on an Editor without it.</summary>
        private static Type PlayModeViewType =>
            typeof(EditorWindow).Assembly.GetType("UnityEditor.PlayModeView", false) ?? GameViewType;

        private static Type GameViewType => typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView", false);

        private static void HideGameViews()
        {
            var viewType = PlayModeViewType;
            if (viewType == null) return;
            var views = Resources.FindObjectsOfTypeAll(viewType);
            if (views.Length == 0) return;
            // Dock this tab beside the Game view first, so a window that held only the Game view keeps a tab and stays open.
            GetWindow<ServerPlayerView>(Title, false, views[0].GetType());
            foreach (var view in views)
                if (view is EditorWindow window && window != null) window.Close();
            NebulaLog.Info("this virtual player hosts the server and renders nothing: closed its Game view, which would build the render pipeline every frame");
        }

        private static void RestoreGameView()
        {
            if (!HasOpenInstances<ServerPlayerView>()) return;
            var viewType = PlayModeViewType;
            if (viewType != null && GameViewType != null && Resources.FindObjectsOfTypeAll(viewType).Length == 0)
                OpenGameViewBesideThisTab();
            foreach (var tab in Resources.FindObjectsOfTypeAll<ServerPlayerView>())
                if (tab != null) tab.Close();
        }

        /// <summary><c>GetWindow&lt;GameView&gt;("Game", true, typeof(ServerPlayerView))</c>; GameView is internal, hence reflection.</summary>
        private static void OpenGameViewBesideThisTab()
        {
            foreach (var method in typeof(EditorWindow).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != nameof(GetWindow) || !method.IsGenericMethodDefinition) continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 3 || parameters[0].ParameterType != typeof(string) || parameters[1].ParameterType != typeof(bool)
                    || parameters[2].ParameterType != typeof(Type[])) continue;
                method.MakeGenericMethod(GameViewType).Invoke(null, new object[] { "Game", true, new[] { typeof(ServerPlayerView) } });
                return;
            }
            GetWindow(GameViewType);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "This virtual player hosts the Nebula server. The server renders nothing, so Nebula closes this player's Game view while it plays. " +
                "A virtual player cannot build the render pipeline on some Unity versions, and its Game view would log errors every frame.\n\n" +
                "Read the server's log in the Console. The Game view comes back when this player next plays as a client.",
                MessageType.Info);
        }
    }
}
