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
    public SpacetimeSettings? Spacetime { get; set; }

    public sealed class UnitySettings
    {
        /// <summary>Explicit Unity executable, or a folder of Hub installs (…/Hub/Editor).</summary>
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

    public sealed class SpacetimeSettings
    {
        /// <summary>Server nickname or URL passed to `spacetime -s`, e.g. maincloud.</summary>
        public string Server { get; set; } = "maincloud";
        /// <summary>Default database name for deploys; nebula.json can override per project.</summary>
        public string? Database { get; set; }
        public string? ConfiguredAt { get; set; }

        public bool IsConfigured => ConfiguredAt != null;
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
