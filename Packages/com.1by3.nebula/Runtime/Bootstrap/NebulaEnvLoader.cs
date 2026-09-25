using UnityEngine;

namespace Nebula
{
    /// <summary>
    /// Connects <see cref="NebulaEnv"/> to the Unity runtime before any scene loads: its warnings go to the Nebula
    /// log, and in the Editor the project's <c>.env.nebula</c> is loaded each time Play starts. That covers every
    /// process the Editor dev loop runs, including the Multiplayer Play Mode virtual player that hosts the server
    /// (a virtual player reads the main project's file, not its copy). Builds never read <c>.env.nebula</c>: the
    /// process that starts a worker hands it its variables.
    /// </summary>
    internal static class NebulaEnvLoader
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Load()
        {
            NebulaEnv.Warn = message => NebulaLog.Warn("env: " + message);
#if UNITY_EDITOR
            string root = EditorDevPaths.ProjectRoot(Application.dataPath);
            if (string.IsNullOrEmpty(root)) return;
            // Called even when the file is gone, so variables an earlier Play copied into this Editor's environment go too.
            int count = NebulaEnv.LoadFile(System.IO.Path.Combine(root, NebulaEnv.LocalFileName));
            if (count > 0) NebulaLog.Info($"env: loaded {count} variable(s) from {NebulaEnv.LocalFileName}");
#endif
        }
    }
}
