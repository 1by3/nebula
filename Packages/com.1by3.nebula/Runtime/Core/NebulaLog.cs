using UnityEngine;

namespace Nebula
{
    /// <summary>Thin logging wrapper so every Nebula line is greppable by prefix in headless logs.</summary>
    public static class NebulaLog
    {
        public static string Prefix = "[nebula]";
        public static bool Verbose;

        public static void Info(string message) => Debug.Log($"{Prefix} {message}");
        public static void Warn(string message) => Debug.LogWarning($"{Prefix} {message}");
        public static void Error(string message) => Debug.LogError($"{Prefix} {message}");

        public static void Debugf(string message)
        {
            if (Verbose) Debug.Log($"{Prefix} {message}");
        }
    }
}
