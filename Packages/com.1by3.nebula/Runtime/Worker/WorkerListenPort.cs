namespace Nebula
{
    /// <summary>
    /// Where a worker listens. In order: the <c>-nebula-port</c> switch, the <c>PORT</c> environment variable, then
    /// <see cref="NebulaConfig.WorkerBasePort"/> plus the worker's index. Nothing else in the mesh has to agree on the
    /// port in advance: the worker registers the one it chose, and gateways and the orchestrator dial that.
    /// <c>PORT</c> is for a host that runs one worker per machine and owns its firewall; a host that starts several
    /// workers on one machine passes <c>-nebula-port</c> to each, so an inherited <c>PORT</c> cannot make them collide.
    /// </summary>
    internal static class WorkerListenPort
    {
        public const string EnvironmentVariable = "PORT";

        public enum Source { CommandLine, Environment, Config }

        public readonly struct Result
        {
            public readonly ushort Port;
            public readonly Source From;
            /// <summary>Why a value that was given was not used; null when nothing was skipped.</summary>
            public readonly string Warning;

            public Result(ushort port, Source from, string warning)
            {
                Port = port;
                From = from;
                Warning = warning;
            }

            /// <summary>How the startup log names where the port came from.</summary>
            public string Describe => From == Source.CommandLine ? "-nebula-port" : From == Source.Environment ? EnvironmentVariable : "WorkerBasePort + index";
        }

        /// <summary>
        /// Picks the port from the raw <c>-nebula-port</c> value and <c>PORT</c> value (null when absent). A value
        /// that is not a whole number from 1 to 65535 is skipped with a warning, and the next source is used.
        /// </summary>
        public static Result Resolve(string commandLineValue, string environmentValue, ushort workerBasePort, ushort workerIndex)
        {
            string warning = null;
            if (commandLineValue != null)
            {
                if (TryParse(commandLineValue, out ushort port)) return new Result(port, Source.CommandLine, null);
                warning = $"ignoring -nebula-port '{commandLineValue}': not a port from 1 to 65535";
            }
            if (!string.IsNullOrEmpty(environmentValue))
            {
                if (TryParse(environmentValue, out ushort port)) return new Result(port, Source.Environment, warning);
                warning = Append(warning, $"ignoring {EnvironmentVariable}='{environmentValue}': not a port from 1 to 65535");
            }
            return new Result((ushort)(workerBasePort + workerIndex), Source.Config, warning);
        }

        private static bool TryParse(string value, out ushort port)
        {
            port = 0;
            if (!int.TryParse(value.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int i) || i < 1 || i > ushort.MaxValue) return false;
            port = (ushort)i;
            return true;
        }

        private static string Append(string first, string second) => first == null ? second : first + "; " + second;
    }
}
