using System;

namespace Nebula
{
    /// <summary>
    /// Where the orchestrator keeps the control plane and saved entities, as one string
    /// (<see cref="NebulaConfig.DatabaseUrl"/>, <c>-nebula-database</c>).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>sqlite:&lt;file&gt;</c>: a SQLite database file (standalone orchestrator; the default for a local mesh).</item>
    /// <item><c>postgres://user:password@host:port/database</c> (or <c>postgresql://</c>, or a <c>Host=...;</c> connection string): a PostgreSQL database (standalone orchestrator; for a deployed mesh).</item>
    /// <item><c>file:&lt;folder&gt;</c>: plain files in a folder (the Unity orchestrator, which carries no database driver).</item>
    /// <item><c>memory</c>: nothing survives the process.</item>
    /// </list>
    /// </remarks>
    public readonly struct DatabaseUrl
    {
        /// <summary><c>sqlite</c>, <c>postgres</c>, <c>file</c> or <c>memory</c>.</summary>
        public readonly string Scheme;
        /// <summary>The file, folder or connection string the scheme applies to ("" for memory).</summary>
        public readonly string Target;
        /// <summary>What was parsed, for logs, with a password masked.</summary>
        public readonly string Display;

        private DatabaseUrl(string scheme, string target, string display)
        {
            Scheme = scheme;
            Target = target;
            Display = display;
        }

        /// <param name="url">The configured value; empty or null means <paramref name="fallback"/>.</param>
        /// <param name="fallback">What an empty value means for this process, e.g. <c>sqlite:/opt/nebula/data/nebula.db</c>.</param>
        /// <exception cref="ArgumentException">The value has no scheme this build understands.</exception>
        public static DatabaseUrl Parse(string url, string fallback)
        {
            string s = string.IsNullOrWhiteSpace(url) ? fallback : url.Trim();
            if (string.IsNullOrWhiteSpace(s)) return new DatabaseUrl("memory", "", "memory");
            if (s.Equals("memory", StringComparison.OrdinalIgnoreCase) || s.Equals("memory:", StringComparison.OrdinalIgnoreCase)) return new DatabaseUrl("memory", "", "memory");
            if (StartsWith(s, "sqlite:")) return new DatabaseUrl("sqlite", StripSlashes(s.Substring("sqlite:".Length)), s);
            if (StartsWith(s, "file:")) return new DatabaseUrl("file", StripSlashes(s.Substring("file:".Length)), s);
            if (StartsWith(s, "postgres://") || StartsWith(s, "postgresql://")) return new DatabaseUrl("postgres", s, Mask(s));
            if (s.IndexOf("Host=", StringComparison.OrdinalIgnoreCase) >= 0 && s.IndexOf(';') >= 0) return new DatabaseUrl("postgres", s, MaskKeyValue(s));
            throw new ArgumentException($"unknown database URL '{s}': expected sqlite:<file>, postgres://..., file:<folder> or memory");
        }

        public override string ToString() => Display;

        private static bool StartsWith(string s, string prefix) => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        /// <summary><c>sqlite:///opt/x.db</c> is the file <c>/opt/x.db</c> and <c>sqlite://x.db</c> the file <c>x.db</c>: the URL's empty authority is dropped.</summary>
        private static string StripSlashes(string path)
        {
            return path.StartsWith("//", StringComparison.Ordinal) ? path.Substring(2) : path;
        }

        /// <summary><c>postgres://user:secret@host/db</c> becomes <c>postgres://user:***@host/db</c>.</summary>
        private static string Mask(string url)
        {
            int at = url.IndexOf('@');
            int scheme = url.IndexOf("://", StringComparison.Ordinal);
            if (at < 0 || scheme < 0) return url;
            int colon = url.IndexOf(':', scheme + 3);
            if (colon < 0 || colon > at) return url;
            return url.Substring(0, colon + 1) + "***" + url.Substring(at);
        }

        private static string MaskKeyValue(string s)
        {
            var parts = s.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].TrimStart();
                if (p.StartsWith("Password=", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Pwd=", StringComparison.OrdinalIgnoreCase))
                    parts[i] = p.Substring(0, p.IndexOf('=') + 1) + "***";
            }
            return string.Join(";", parts);
        }
    }
}
