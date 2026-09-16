using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nebula.Cli.Core;

/// <summary>~/.nebula-cli/config.json: machine-wide settings and provider credentials.</summary>
public sealed class CliConfig
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>A checkout of the Nebula repository `nebula init --embed` copies the package from. Set by the from-source installer.</summary>
    public string? SdkSource { get; set; }
    public string? SetupCompletedAt { get; set; }
    public UnitySettings Unity { get; set; } = new();
    public HetznerSettings? Hetzner { get; set; }
    public DatabaseSettings? Database { get; set; }
    /// <summary>Credentials `nebula cloud login` stored for Nebula Cloud.</summary>
    public CloudSettings? Cloud { get; set; }

    public sealed class UnitySettings
    {
        /// <summary>Explicit Unity executable, or a folder of Hub installs (â€¦/Hub/Editor).</summary>
        public string? Editor { get; set; }
    }

    public sealed class HetznerSettings
    {
        public string? Token { get; set; }
        public string? Project { get; set; }
        public string Location { get; set; } = "ash";
        public string WorkerType { get; set; } = "cpx21";
        public string OrchestratorType { get; set; } = "cpx21";
        public string Image { get; set; } = "ubuntu-24.04";
        public string? ConfiguredAt { get; set; }

        public bool IsConfigured => !string.IsNullOrEmpty(ResolveToken());

        /// <summary>HCLOUD_TOKEN in the environment wins over the stored token.</summary>
        public string? ResolveToken()
        {
            var env = Environment.GetEnvironmentVariable("HCLOUD_TOKEN");
            return !string.IsNullOrEmpty(env) ? env : Token;
        }
    }

    /// <summary>The database a deployed orchestrator stores the control plane and saved entities in.</summary>
    public sealed class DatabaseSettings
    {
        /// <summary>postgres://user:password@host/db for a PostgreSQL server the orchestrator VM can reach, or sqlite:&lt;file on the VM&gt;. Null = SQLite on the orchestrator VM.</summary>
        public string? Url { get; set; }
        public string? ConfiguredAt { get; set; }

        /// <summary>NEBULA_DATABASE_URL in the environment wins over the stored URL.</summary>
        public static string? ResolveUrl(DatabaseSettings? settings)
        {
            var env = Environment.GetEnvironmentVariable("NEBULA_DATABASE_URL");
            return !string.IsNullOrEmpty(env) ? env : settings?.Url;
        }
    }

    /// <summary>The Nebula Cloud session: where the API is and the tokens `nebula cloud login` obtained.</summary>
    public sealed class CloudSettings
    {
        public const string DefaultApiUrl = "https://api.nebula.1by3.co";

        /// <summary>Origin of the Cloud API (no /v1); the CLI adds the version prefix.</summary>
        public string? ApiUrl { get; set; }
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public CloudUser? User { get; set; }
        public string? LoggedInAt { get; set; }

        public bool IsLoggedIn => !string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(RefreshToken);

        /// <summary>NEBULA_CLOUD_API in the environment wins over the stored URL, which wins over the default.</summary>
        public static string ResolveApiUrl(CloudSettings? settings, string? explicitUrl = null)
        {
            var env = Environment.GetEnvironmentVariable("NEBULA_CLOUD_API");
            string url = !string.IsNullOrEmpty(env) ? env : explicitUrl ?? settings?.ApiUrl ?? DefaultApiUrl;
            return NormalizeApiUrl(url);
        }

        /// <summary>Accept the documented base URL (…/v1) or its origin; the client appends /v1 itself.</summary>
        public static string NormalizeApiUrl(string url)
        {
            url = url.Trim().TrimEnd('/');
            if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) url = url.Substring(0, url.Length - 3);
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new CliError($"'{url}' is not an http(s) URL", "pass the Cloud API as https://host, or set NEBULA_CLOUD_API");
            return url;
        }

        public sealed class CloudUser
        {
            public string? Id { get; set; }
            public string? Email { get; set; }
            public string? Name { get; set; }
        }
    }

    public static CliConfig Load()
    {
        string path = Platform.ConfigPath;
        if (!File.Exists(path)) return new CliConfig();
        try
        {
            return JsonSerializer.Deserialize<CliConfig>(File.ReadAllText(path), Json) ?? new CliConfig();
        }
        catch (JsonException e)
        {
            throw new CliError($"{path} is not valid JSON: {e.Message}", "fix or delete the file");
        }
    }

    public void Save()
    {
        string path = Platform.ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + Environment.NewLine);
        Platform.MakePrivate(path);
    }
}
