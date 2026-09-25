using System;
using System.Collections.Generic;
using System.IO;

namespace Nebula.Hosting
{
    /// <summary>
    /// The game's environment variables a worker host gives every worker it launches, in this order (a later layer
    /// wins over an earlier one):
    /// <list type="number">
    /// <item>The service manifest's <c>Env</c> map (<see cref="ManifestEnv"/>): the non-secret variables of a
    /// self-hosted deployment, written by <c>nebula deploy</c> from <c>deploy.env</c> in nebula.json.</item>
    /// <item>The dotenv file the host's own <c>NEBULA_ENV_FILE</c> names: secrets a platform keeps out of the build
    /// (Nebula Cloud), or the project's <c>.env.nebula</c> under <c>nebula start</c>.</item>
    /// </list>
    /// Reserved names (<c>PORT</c>, <c>NEBULA_*</c>) are dropped with a warning; values are never logged.
    /// </summary>
    public static class WorkerEnvironment
    {
        /// <summary>The service manifest's <c>Env</c> map, set by the standalone orchestrator when it loads the manifest. Null when there is none.</summary>
        public static IDictionary<string, string> ManifestEnv;

        /// <summary>
        /// The variables to give a worker. <paramref name="getEnvironment"/> reads the launcher's own environment
        /// (null reads the process's); <paramref name="warn"/> gets one line per variable left out.
        /// </summary>
        public static Dictionary<string, string> Resolve(Func<string, string> getEnvironment = null, Action<string> warn = null)
        {
            getEnvironment = getEnvironment ?? Environment.GetEnvironmentVariable;
            var result = NebulaEnv.Filter(ManifestEnv, "service manifest Env", warn);
            string file = getEnvironment(NebulaEnv.EnvFileVariable);
            if (!string.IsNullOrEmpty(file))
            {
                if (!File.Exists(file)) warn?.Invoke($"{NebulaEnv.EnvFileVariable} names a file that does not exist; workers get no variables from it");
                else
                {
                    try
                    {
                        foreach (var kv in NebulaEnv.ReadFile(file, warn)) result[kv.Key] = kv.Value;
                    }
                    catch (Exception e) { warn?.Invoke($"could not read the file {NebulaEnv.EnvFileVariable} names ({e.GetType().Name}); workers get no variables from it"); }
                }
            }
            return result;
        }

        /// <summary>Put <paramref name="vars"/> into a child process's environment (<see cref="System.Diagnostics.ProcessStartInfo.Environment"/>).</summary>
        public static void ApplyTo(IDictionary<string, string> target, IDictionary<string, string> vars)
        {
            foreach (var kv in vars) target[kv.Key] = kv.Value;
        }
    }
}
