using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
#if UNITY_6000_6_OR_NEWER
using Unity.PlayMode.Editor;
#endif

namespace Nebula.Editor
{
    /// <summary>One Editor instance of a Play Mode Scenario, as <see cref="DevLoopScenario"/> reads it from the scenario asset.</summary>
    internal readonly struct ScenarioEditorInstance
    {
        public readonly string Name;
        /// <summary>True for the main Editor, false for an additional Editor instance (a virtual player).</summary>
        public readonly bool IsMainEditor;
        /// <summary>The Multiplayer Play Mode player slot the instance runs in: 0 is the main Editor, 1 is Player 2, and so on.</summary>
        public readonly int PlayerIndex;
        /// <summary>The Multiplayer Play Mode tag the scenario gives the instance when it starts; empty for none.</summary>
        public readonly string Tag;
        /// <summary>Whether the instance is under Manual Control (started with its Activate button) rather than Scenario Control.</summary>
        public readonly bool ManualControl;

        public ScenarioEditorInstance(string name, bool isMainEditor, int playerIndex, string tag, bool manualControl)
        {
            Name = name;
            IsMainEditor = isMainEditor;
            PlayerIndex = playerIndex;
            Tag = tag ?? "";
            ManualControl = manualControl;
        }

        /// <summary>Whether the scenario tags this instance <see cref="EditorRunPlan.ServerTag"/> (any case), so it hosts the Nebula server.</summary>
        public bool HostsServer => string.Equals(Tag.Trim(), EditorRunPlan.ServerTag, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The decisions of <see cref="DevLoopScenario"/>, apart from the Editor, so they can be tested without Multiplayer Play Mode.</summary>
    internal static class DevLoopScenarioRules
    {
        /// <summary>
        /// The dev-loop scenario among the project's Play Mode Scenario assets: the one whose instances include one
        /// that <see cref="ScenarioEditorInstance.HostsServer"/>. With several, the first by asset path (ordinal) wins,
        /// so every Editor picks the same one, and <paramref name="warning"/> names the others. Null when none hosts the server.
        /// </summary>
        /// <param name="candidates">Each scenario's asset path and its Editor instances.</param>
        public static string Choose(IReadOnlyList<KeyValuePair<string, IReadOnlyList<ScenarioEditorInstance>>> candidates, out string warning)
        {
            warning = null;
            var hosting = new List<string>();
            if (candidates != null)
                foreach (var candidate in candidates)
                    if (!string.IsNullOrEmpty(candidate.Key) && ServerInstance(candidate.Value) >= 0) hosting.Add(candidate.Key);
            if (hosting.Count == 0) return null;
            hosting.Sort(StringComparer.Ordinal);
            if (hosting.Count > 1)
                warning = $"{hosting.Count} Play Mode Scenarios start an Editor instance tagged {EditorRunPlan.ServerTag} ({string.Join(", ", hosting)}); using {hosting[0]}. " +
                          "Choose another in the scenario dropdown next to the Play button, or remove the tag from the ones you don't use.";
            return hosting[0];
        }

        /// <summary>The position in <paramref name="instances"/> of the first one that hosts the server, or -1.</summary>
        public static int ServerInstance(IReadOnlyList<ScenarioEditorInstance> instances)
        {
            if (instances == null) return -1;
            for (int i = 0; i < instances.Count; i++)
                if (instances[i].HostsServer) return i;
            return -1;
        }

        /// <summary>
        /// Whether to make the dev-loop scenario the active one. Only the main Editor switches, only under
        /// <see cref="NebulaEditorRunMode.MultiplayerPlayMode"/>, only away from Unity's Default scenario (a scenario
        /// someone chose stays), only while no scenario is starting or running and the Editor is not entering Play,
        /// and only when no virtual player is running: making a scenario active stops every virtual player that the
        /// Multiplayer Play Mode window started. <paramref name="virtualPlayerActive"/> is null when that could not be
        /// read, which never switches.
        /// </summary>
        public static bool ShouldSwitch(NebulaEditorRunMode mode, bool isMainEditor, bool activeIsDefault, bool scenarioIdle, bool enteringOrPlaying,
            bool? virtualPlayerActive, bool haveScenario)
        {
            return mode == NebulaEditorRunMode.MultiplayerPlayMode && isMainEditor && activeIsDefault && scenarioIdle && !enteringOrPlaying
                   && virtualPlayerActive == false && haveScenario;
        }
    }

    /// <summary>
    /// Starts the server of the Multiplayer Play Mode dev loop without anyone ticking a virtual player. Under
    /// <see cref="NebulaEditorRunMode.MultiplayerPlayMode"/>, a virtual player hosts the server, and Unity's
    /// Multiplayer Play Mode window has to have one enabled before Play. A Play Mode Scenario can start that
    /// virtual player itself: a scenario asset (Window > Play Mode > Scenarios, or <c>Nebula &gt; Dev Loop &gt; Create Scenario</c>)
    /// whose Editor instance is tagged <c>Server</c>. When the active scenario is Unity's Default, no virtual player
    /// is running, and exactly that kind of scenario exists, this makes it the active scenario, so the next Play
    /// starts the server's Editor instance. It checks when the Editor loads, after each Play, and when the Nebula
    /// config or a scenario asset changes. The Play button reads the active scenario before Play mode starts, so
    /// switching later would miss that Play.
    /// </summary>
    /// <remarks>
    /// Needs Unity 6.6 (6000.6) or later, where Play Mode Scenarios are part of the Editor, with Multiplayer Play Mode
    /// available. Some of what it reads (the scenario's instances, the virtual players' state) is internal to
    /// Unity: it reads that by reflection or from the scenario's serialized data, and when that fails it logs one
    /// warning and changes nothing. Once a scenario is active, enter Play with the Play button or
    /// <c>PlayModeScenarioManager.Start()</c>: Unity switches back to the Default scenario when a script sets
    /// <c>EditorApplication.isPlaying</c> while a scenario is active.
    /// </remarks>
    [InitializeOnLoad]
    internal static class DevLoopScenario
    {
        private const string MenuRoot = "Nebula/Dev Loop/";
        /// <summary>The scenario <see cref="CreateScenario"/> makes; Unity keeps scenarios in <c>Assets/Settings/PlayMode</c>.</summary>
        private const string ScenarioName = "Nebula Dev Loop";
        /// <summary>The player slot <see cref="CreateScenario"/> gives the server: Player 2.</summary>
        private const int ServerPlayerIndex = 1;
        /// <summary>How long to wait for Multiplayer Play Mode to finish loading before giving up on a check.</summary>
        private const double MaxWaitSeconds = 20;

        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static bool _warned;
#if UNITY_6000_6_OR_NEWER
        private static bool _scheduled;
        private static double _waitUntil;
#endif

        static DevLoopScenario()
        {
#if UNITY_6000_6_OR_NEWER
            if (Application.isBatchMode) return;
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.EnteredEditMode) Schedule();
            };
            // Not from the static constructor itself: assets may not load yet while the domain initializes.
            EditorApplication.delayCall += Schedule;
#endif
        }

        /// <summary>Check once Multiplayer Play Mode has loaded (it loads after the domain), and at most once per Editor update.</summary>
        internal static void Schedule()
        {
#if UNITY_6000_6_OR_NEWER
            if (Application.isBatchMode || _scheduled) return;
            var config = Resources.Load<NebulaConfig>("NebulaConfig");
            if (config == null || config.EditorRunMode != NebulaEditorRunMode.MultiplayerPlayMode) return;
            _scheduled = true;
            _waitUntil = EditorApplication.timeSinceStartup + MaxWaitSeconds;
            EditorApplication.update += WaitThenApply;
#endif
        }

#if UNITY_6000_6_OR_NEWER
        private static void WaitThenApply()
        {
            if (!PlayModeLoaded() && EditorApplication.timeSinceStartup < _waitUntil) return;
            EditorApplication.update -= WaitThenApply;
            _scheduled = false;
            Apply(fromMenu: false);
        }

        /// <summary>Whether Multiplayer Play Mode has set up its players, so the rule can tell whether any is running.</summary>
        private static bool PlayModeLoaded()
        {
            try
            {
                var type = MultiplayerType("MultiplayerPlaymode");
                var initialized = type?.GetProperty("IsVirtualProjectWorkflowInitialized", AnyStatic);
                return initialized != null && (bool)initialized.GetValue(null);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Make the dev-loop scenario active when <see cref="DevLoopScenarioRules.ShouldSwitch"/> says so; returns the scenario that is active afterwards.</summary>
        private static PlayModeScenario Apply(bool fromMenu)
        {
            var active = PlayModeScenarioManager.ActiveScenario;
            var config = Resources.Load<NebulaConfig>("NebulaConfig");
            if (config == null)
            {
                if (fromMenu) NebulaLog.Warn("no NebulaConfig in a Resources folder (Nebula > Select Config creates one); the dev loop needs EditorRunMode MultiplayerPlayMode");
                return active;
            }
            bool isMainEditor;
            try
            {
                isMainEditor = Unity.Multiplayer.PlayMode.CurrentPlayer.IsMainEditor;
            }
            catch (Exception)
            {
                // CurrentPlayer throws when Multiplayer Play Mode is not available (the package is missing, or this is batch mode).
                if (fromMenu) NebulaLog.Warn("Multiplayer Play Mode is not available in this Editor: add the com.unity.multiplayer.playmode package with the Package Manager");
                return active;
            }
            // A virtual player never switches (its project copy is read-only), so it doesn't look for scenarios either.
            if (!isMainEditor) return active;
            string path = FindDevLoopScenario(out var warning);
            var scenario = path != null ? AssetDatabase.LoadAssetAtPath<PlayModeScenario>(path) : null;
            bool? virtualPlayerActive = AnyVirtualPlayerActive();
            if (!DevLoopScenarioRules.ShouldSwitch(config.EditorRunMode, isMainEditor, IsDefault(active), PlayModeScenarioManager.State == PlayModeScenarioState.Idle,
                    EditorApplication.isPlayingOrWillChangePlaymode, virtualPlayerActive, scenario != null))
                return active;
            if (warning != null) NebulaLog.Warn(warning);
            try
            {
                PlayModeScenarioManager.ActiveScenario = scenario;
            }
            catch (Exception e)
            {
                WarnOnce($"could not make the Play Mode Scenario {path} active ({e.GetType().Name}: {e.Message}); choose it in the scenario dropdown next to the Play button");
                return PlayModeScenarioManager.ActiveScenario;
            }
            NebulaLog.Info($"dev loop: made the Play Mode Scenario '{scenario.name}' active, so Play starts the Editor instance tagged {EditorRunPlan.ServerTag} that hosts the server ({path})");
            return scenario;
        }

        private static bool IsDefault(PlayModeScenario scenario) =>
            scenario == null || scenario.GetType().Name == "DefaultScenario" || !EditorUtility.IsPersistent(scenario);

        /// <summary>The asset path of the scenario <see cref="DevLoopScenarioRules.Choose"/> picks, or null.</summary>
        internal static string FindDevLoopScenario(out string warning)
        {
            var candidates = new List<KeyValuePair<string, IReadOnlyList<ScenarioEditorInstance>>>();
            foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(PlayModeScenario)))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var scenario = AssetDatabase.LoadAssetAtPath<PlayModeScenario>(path);
                if (scenario == null || !IsOrchestrated(scenario)) continue;
                var instances = ReadEditorInstances(scenario);
                if (instances != null) candidates.Add(new KeyValuePair<string, IReadOnlyList<ScenarioEditorInstance>>(path, instances));
            }
            return DevLoopScenarioRules.Choose(candidates, out warning);
        }

        /// <summary>Whether the scenario is Unity's scenario of instances (<c>OrchestratedScenario</c>, internal), the kind the Scenarios window creates.</summary>
        private static bool IsOrchestrated(PlayModeScenario scenario) => scenario.GetType().Name == "OrchestratedScenario";

        /// <summary>
        /// The Editor instances of a scenario, from its serialized data (<c>m_Settings.m_InstanceItems</c>: one managed
        /// reference per instance, whose type names its controller). Null, with one warning, when the data is not laid
        /// out that way, as it may not be on another Unity version.
        /// </summary>
        internal static List<ScenarioEditorInstance> ReadEditorInstances(PlayModeScenario scenario)
        {
            using (var serialized = new SerializedObject(scenario))
            {
                var items = serialized.FindProperty("m_Settings.m_InstanceItems");
                if (items == null || !items.isArray)
                {
                    WarnOnce($"could not read the instances of the Play Mode Scenario {AssetDatabase.GetAssetPath(scenario)} on this Unity version; Nebula won't make it active. Choose it in the scenario dropdown next to the Play button.");
                    return null;
                }
                var instances = new List<ScenarioEditorInstance>();
                for (int i = 0; i < items.arraySize; i++)
                {
                    var item = items.GetArrayElementAtIndex(i);
                    if (item.propertyType != SerializedPropertyType.ManagedReference) continue;
                    string type = item.managedReferenceFullTypename ?? "";
                    bool main = type.Contains("MainEditorController");
                    if (!main && !type.Contains("CloneEditorController")) continue;
                    string tag = item.FindPropertyRelative("m_Settings.PlayerTag")?.stringValue;
                    int index = main ? 0 : item.FindPropertyRelative("m_Settings.PlayerInstanceIndex")?.intValue ?? -1;
                    var runMode = item.FindPropertyRelative("m_RunMode");
                    bool manual = runMode != null && (runMode.propertyType == SerializedPropertyType.Enum ? runMode.enumValueIndex : runMode.intValue) == 1;
                    instances.Add(new ScenarioEditorInstance(item.FindPropertyRelative("m_Name")?.stringValue, main, index, tag, manual));
                }
                return instances;
            }
        }

        /// <summary>Whether a virtual player (not the main Editor) is launching or running; null when that could not be read.</summary>
        private static bool? AnyVirtualPlayerActive()
        {
            try
            {
                if (!(MultiplayerType("MultiplayerPlaymode")?.GetProperty("Players", AnyStatic)?.GetValue(null) is Array players)) return null;
                foreach (var player in players)
                {
                    if (player == null) continue;
                    var type = player.GetType();
                    if (type.GetProperty("Type", AnyInstance)?.GetValue(player)?.ToString() == "Main") continue;
                    if (IsLaunchingOrLaunched(player)) return true;
                }
                return false;
            }
            catch (Exception e)
            {
                WarnOnce($"could not read Multiplayer Play Mode's virtual players on this Unity version ({e.GetType().Name}); Nebula won't change the active Play Mode Scenario");
                return null;
            }
        }

        private static bool IsLaunchingOrLaunched(object player)
        {
            string state = player.GetType().GetProperty("PlayerState", AnyInstance)?.GetValue(player)?.ToString();
            return state == "Launching" || state == "Launched";
        }

        /// <summary>An internal type of Unity's Multiplayer Play Mode, from the Editor's multiplayer module; null when it has none by that name.</summary>
        private static Type MultiplayerType(string name) =>
            typeof(Unity.Multiplayer.Editor.EditorMultiplayerRolesManager).Assembly.GetType("Unity.Multiplayer.PlayMode.Editor." + name, false);
#endif

        /// <summary>
        /// Start the virtual player that hosts the server now, before Play, so that pressing Play later only has to
        /// enter Play mode. Makes the dev-loop scenario active first when it can. The virtual player boots and imports
        /// in its own Editor process, which takes a while the first time; press Play when it is up. With the
        /// instance's Keep Active setting on, it stays up after Play stops. An instance under Manual Control starts
        /// the way its Activate button starts it; one under Scenario Control starts in its player slot as the
        /// scenario starts it, and the scenario takes it over at Play.
        /// </summary>
        [MenuItem(MenuRoot + "Start Server Player", priority = 46)]
        public static void StartServerPlayer()
        {
#if UNITY_6000_6_OR_NEWER
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                NebulaLog.Warn("the Editor is playing: the server player is already started, or starts with this Play");
                return;
            }
            var active = Apply(fromMenu: true);
            if (active == null || IsDefault(active))
            {
                if (AnyVirtualPlayerActive() == true)
                    NebulaLog.Info("a virtual player from the Multiplayer Play Mode window is already running: press Play and it hosts the server");
                else
                    NebulaLog.Warn($"no Play Mode Scenario is active that starts an Editor instance tagged {EditorRunPlan.ServerTag}. Create one with Nebula > Dev Loop > Create Scenario " +
                                   "(or Window > Play Mode > Scenarios), check that NebulaConfig.EditorRunMode is MultiplayerPlayMode, then try again.");
                return;
            }
            if (!IsOrchestrated(active))
            {
                NebulaLog.Warn($"the active Play Mode Scenario '{active.name}' is not a scenario of Editor instances; choose one that starts an Editor instance tagged {EditorRunPlan.ServerTag}");
                return;
            }
            var instances = ReadEditorInstances(active);
            if (instances == null) return;
            int server = DevLoopScenarioRules.ServerInstance(instances);
            if (server < 0)
            {
                NebulaLog.Warn($"the active Play Mode Scenario '{active.name}' has no Editor instance tagged {EditorRunPlan.ServerTag}; tag the instance that should host the server in Window > Play Mode > Scenarios");
                return;
            }
            var instance = instances[server];
            if (instance.IsMainEditor)
            {
                NebulaLog.Info($"in the Play Mode Scenario '{active.name}' the main Editor hosts the server: there is no virtual player to start, press Play");
                return;
            }
            try
            {
                if (instance.ManualControl) ActivateManualInstance(active, instance);
                else ActivatePlayerSlot(instance);
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                NebulaLog.Warn($"could not start the server player '{instance.Name}' on this Unity version ({inner.GetType().Name}: {inner.Message}); press Play and the scenario starts it");
            }
#else
            NebulaLog.Warn("Start Server Player needs Unity 6.6 (6000.6) or later, where Play Mode Scenarios are part of the Editor. Enable a virtual player in Window > Multiplayer > Multiplayer Play Mode instead.");
#endif
        }

        /// <summary>
        /// Create a Play Mode Scenario for the dev loop, the way the Scenarios window's own New button creates one, and
        /// make it active: the main Editor (untagged, so it is the client) and one additional Editor instance, Player 2,
        /// tagged <c>Server</c>, with Stream Logs to Main Editor and, for you, Keep Active on. Commit the asset
        /// (<c>Assets/Settings/PlayMode/Nebula Dev Loop.asset</c>) so everyone on the project gets it; Keep Active is
        /// a per-user setting kept in <c>UserSettings</c>. Selects the existing scenario instead when one already
        /// starts an Editor instance tagged <c>Server</c>.
        /// </summary>
        [MenuItem(MenuRoot + "Create Scenario", priority = 47)]
        public static void CreateScenario()
        {
#if UNITY_6000_6_OR_NEWER
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                NebulaLog.Warn("stop Play before creating the dev loop's Play Mode Scenario");
                return;
            }
            string existing = FindDevLoopScenario(out _);
            if (existing != null)
            {
                NebulaLog.Info($"the Play Mode Scenario {existing} already starts an Editor instance tagged {EditorRunPlan.ServerTag}; selected it");
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<PlayModeScenario>(existing);
                Apply(fromMenu: true);
                return;
            }
            PlayModeScenario scenario;
            try
            {
                scenario = CreateScenarioAsset();
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                NebulaLog.Warn($"could not create a Play Mode Scenario on this Unity version ({inner.GetType().Name}: {inner.Message}). Create it in Window > Play Mode > Scenarios: " +
                               $"keep the main Editor untagged, add one Editor instance tagged {EditorRunPlan.ServerTag}, and turn on Keep Active and Stream Logs to Main Editor.");
                return;
            }
            string path = AssetDatabase.GetAssetPath(scenario);
            Selection.activeObject = scenario;
            NebulaLog.Info($"created the Play Mode Scenario {path}: the main Editor is the client and Player 2, tagged {EditorRunPlan.ServerTag}, hosts the server. Commit it so everyone on the project gets it.");
            var nowActive = Apply(fromMenu: true);
            if (nowActive != scenario && IsDefault(nowActive) && AnyVirtualPlayerActive() == true)
                NebulaLog.Info("a virtual player from the Multiplayer Play Mode window is running, so the Default scenario stays active: choose the new scenario in the dropdown next to the Play button when you're ready");
#else
            NebulaLog.Warn("Create Scenario needs Unity 6.6 (6000.6) or later, where Play Mode Scenarios are part of the Editor. Enable a virtual player in Window > Multiplayer > Multiplayer Play Mode instead.");
#endif
        }

        [MenuItem(MenuRoot + "Start Server Player", true)]
        [MenuItem(MenuRoot + "Create Scenario", true)]
        private static bool CanUseScenarios() => !EditorApplication.isPlayingOrWillChangePlaymode;

#if UNITY_6000_6_OR_NEWER
        /// <summary>
        /// The scenario asset, through Unity's internal <c>PlayModeScenarioUtils.CreatePlayModeConfig</c> (what the
        /// Scenarios window's New button calls) and <c>OrchestratedScenarioSettings.AddInstance</c>. Throws when any
        /// of it is missing.
        /// </summary>
        internal static PlayModeScenario CreateScenarioAsset()
        {
            var utils = typeof(PlayModeScenarioManager).Assembly.GetType("Unity.PlayMode.Editor.PlayModeScenarioUtils", true);
            var orchestrated = MultiplayerType("OrchestratedScenario") ?? throw new MissingMemberException("OrchestratedScenario");
            var controller = MultiplayerType("CloneEditorController") ?? throw new MissingMemberException("CloneEditorController");
            var settingsType = controller.GetNestedType("InstanceSettings", BindingFlags.Public | BindingFlags.NonPublic) ?? throw new MissingMemberException("CloneEditorController.InstanceSettings");
            var create = utils.GetMethod("CreatePlayModeConfig", AnyStatic, null, new[] { typeof(string), typeof(Type) }, null) ?? throw new MissingMethodException("PlayModeScenarioUtils.CreatePlayModeConfig");

            string name = ScenarioName;
            for (int n = 2; File.Exists(Path.Combine(EditorDevPaths.ProjectRoot(Application.dataPath), "Assets", "Settings", "PlayMode", name + ".asset")); n++) name = $"{ScenarioName} {n}";
            if (!(create.Invoke(null, new object[] { name, orchestrated }) is PlayModeScenario scenario))
                throw new InvalidOperationException("PlayModeScenarioUtils.CreatePlayModeConfig returned no scenario");

            // OrchestratedScenario.m_Settings is a struct: add the instance to a boxed copy, then store the copy back.
            var settingsField = orchestrated.GetField("m_Settings", AnyInstance) ?? throw new MissingFieldException("OrchestratedScenario.m_Settings");
            object settings = settingsField.GetValue(scenario);
            MethodInfo add = null;
            foreach (var method in settings.GetType().GetMethods(AnyInstance))
                if (method.Name == "AddInstance" && method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 2 && method.GetParameters().Length == 2
                    && method.GetParameters()[0].ParameterType == typeof(string)) add = method;
            if (add == null) throw new MissingMethodException("OrchestratedScenarioSettings.AddInstance");

            object instanceSettings = Activator.CreateInstance(settingsType);
            SetField(settingsType, ref instanceSettings, "PlayerTag", EditorRunPlan.ServerTag);
            SetField(settingsType, ref instanceSettings, "PlayerInstanceIndex", ServerPlayerIndex);
            SetField(settingsType, ref instanceSettings, "StreamLogsToMainEditor", true);
            add.MakeGenericMethod(controller, settingsType).Invoke(settings, new[] { $"Player {ServerPlayerIndex + 1}", instanceSettings });
            settingsField.SetValue(scenario, settings);
            EditorUtility.SetDirty(scenario);
            AssetDatabase.SaveAssets();
            TryKeepActive(scenario, settings, controller);
            return scenario;
        }

        private static void SetField(Type type, ref object boxed, string name, object value) =>
            (type.GetField(name, AnyInstance) ?? throw new MissingFieldException(type.Name + "." + name)).SetValue(boxed, value);

        /// <summary>Turn on Keep Active for the new server instance: a per-user setting (<c>UserSettings/OrchestratedScenarioUserSettings.asset</c>). Optional, so a failure only says how to do it by hand.</summary>
        private static void TryKeepActive(PlayModeScenario scenario, object settings, Type controller)
        {
            try
            {
                var userSettingsStore = MultiplayerType("OrchestratedScenarioUserSettings") ?? throw new MissingMemberException("OrchestratedScenarioUserSettings");
                var userSettingsType = controller.GetNestedType("UserSettings", BindingFlags.Public | BindingFlags.NonPublic) ?? throw new MissingMemberException("CloneEditorController.UserSettings");
                var count = (int)settings.GetType().GetProperty("InstanceCount", AnyInstance).GetValue(settings);
                object item = settings.GetType().GetMethod("get_Item", AnyInstance).Invoke(settings, new object[] { count - 1 });
                object userSettings = Activator.CreateInstance(userSettingsType);
                SetField(userSettingsType, ref userSettings, "KeepAliveEnabled", true);
                MethodInfo set = null;
                foreach (var method in userSettingsStore.GetMethods(AnyStatic))
                    if (method.Name == "SetSettings" && method.IsGenericMethodDefinition && method.GetParameters().Length == 3) set = method;
                if (set == null) throw new MissingMethodException("OrchestratedScenarioUserSettings.SetSettings");
                set.MakeGenericMethod(userSettingsType).Invoke(null, new[] { scenario, item, userSettings });
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                NebulaLog.Warn($"created the scenario, but could not turn on Keep Active for its server instance ({inner.GetType().Name}); turn it on in Window > Play Mode > Scenarios so the server stays up between Plays");
            }
        }

        /// <summary>Launch the instance's Multiplayer Play Mode player slot with the argument its scenario launches it with.</summary>
        private static void ActivatePlayerSlot(ScenarioEditorInstance instance)
        {
            var playerType = MultiplayerType("UnityPlayer") ?? throw new MissingMemberException("UnityPlayer");
            if (!(MultiplayerType("MultiplayerPlaymode")?.GetProperty("Players", AnyStatic)?.GetValue(null) is Array players))
                throw new InvalidOperationException("Multiplayer Play Mode has no players yet");
            if (instance.PlayerIndex <= 0 || instance.PlayerIndex >= players.Length || players.GetValue(instance.PlayerIndex) == null)
                throw new InvalidOperationException($"no player slot {instance.PlayerIndex}");
            var player = players.GetValue(instance.PlayerIndex);
            if (IsLaunchingOrLaunched(player))
            {
                NebulaLog.Info($"the server player '{instance.Name}' is already running: press Play");
                return;
            }
            MethodInfo activate = null;
            foreach (var method in playerType.GetMethods(AnyInstance))
                if (method.Name == "Activate" && method.GetParameters().Length == 2) activate = method;
            if (activate == null) throw new MissingMethodException("UnityPlayer.Activate");
            var args = new object[] { null, new List<string> { "-scenarioClone" } };
            if ((bool)activate.Invoke(player, args))
                NebulaLog.Info($"starting the server player '{instance.Name}'. It boots in its own Editor window, which takes a while the first time; press Play once it is up.");
            else
                NebulaLog.Warn($"Multiplayer Play Mode could not start the server player '{instance.Name}' ({args[0]}); press Play and the scenario tries again");
        }

        /// <summary>Start an instance under Manual Control the way its Activate button in Window > Play Mode > Scenarios does.</summary>
        private static void ActivateManualInstance(PlayModeScenario scenario, ScenarioEditorInstance instance)
        {
            var orchestrated = scenario.GetType();
            object runtime = orchestrated.GetProperty("Scenario", AnyInstance)?.GetValue(scenario) ?? throw new InvalidOperationException("the scenario is not loaded");
            object target = runtime.GetType().GetMethod("GetInstanceByName", AnyInstance, null, new[] { typeof(string) }, null)?.Invoke(runtime, new object[] { instance.Name })
                            ?? throw new InvalidOperationException($"the scenario has no instance '{instance.Name}'");
            var start = target.GetType().GetMethod("StartOrResumeAsFreeRunning", AnyInstance, null, new[] { typeof(bool) }, null) ?? throw new MissingMethodException("Instance.StartOrResumeAsFreeRunning");
            if (start.Invoke(target, new object[] { false }) is System.Threading.Tasks.Task task)
                task.ContinueWith(t => NebulaLog.Warn($"the server player '{instance.Name}' failed to start: {t.Exception?.GetBaseException().Message}"),
                    System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
            NebulaLog.Info($"starting the server player '{instance.Name}' (Manual Control). It boots in its own Editor window; press Play once it is up.");
        }
#endif

        private static void WarnOnce(string message)
        {
            if (_warned) return;
            _warned = true;
            NebulaLog.Warn(message);
        }

        /// <summary>Check again when the Nebula config or a Play Mode Scenario asset is imported, moved or deleted.</summary>
        private sealed class ScenarioAssetWatcher : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
#if UNITY_6000_6_OR_NEWER
                if (Application.isBatchMode) return;
                if (Concerns(imported) || Concerns(deleted) || Concerns(moved)) EditorApplication.delayCall += Schedule;
#endif
            }

#if UNITY_6000_6_OR_NEWER
            private static bool Concerns(IEnumerable paths)
            {
                foreach (string path in paths)
                {
                    if (string.IsNullOrEmpty(path) || !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetFileNameWithoutExtension(path) == "NebulaConfig") return true;
                    var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                    if (type == null || typeof(PlayModeScenario).IsAssignableFrom(type)) return true;
                }
                return false;
            }
#endif
        }
    }
}
