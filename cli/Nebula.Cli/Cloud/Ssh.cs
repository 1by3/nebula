namespace Nebula.Cli.Cloud;

using Nebula.Cli.Core;

/// <summary>ssh/scp to the mesh VMs with the CLI's own key and known-hosts file (never the user's defaults).</summary>
public sealed class Ssh
{
    public string KeyFile { get; }
    public string KnownHosts { get; }

    public Ssh(string keyFile)
    {
        KeyFile = keyFile;
        KnownHosts = Path.Combine(Platform.Home, ".ssh", "nebula_known_hosts");
    }

    public static string SshExe => Shell.Which("ssh") ?? throw new CliError("ssh is not installed", "install OpenSSH (on Windows: Settings > Apps > Optional features > OpenSSH Client)");
    public static string ScpExe => Shell.Which("scp") ?? throw new CliError("scp is not installed", "install OpenSSH (on Windows: Settings > Apps > Optional features > OpenSSH Client)");

    private IEnumerable<string> CommonArgs() => new[]
    {
        "-i", KeyFile,
        "-o", "StrictHostKeyChecking=accept-new",
        "-o", $"UserKnownHostsFile={KnownHosts}",
        "-o", "ConnectTimeout=10",
    };

    /// <summary>Generate the key pair if it does not exist yet. Returns the public key text.</summary>
    public string EnsureKey()
    {
        string pub = KeyFile + ".pub";
        if (!File.Exists(pub))
        {
            Ui.Info($"generating ssh key {KeyFile}");
            Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
            string keygen = Shell.Which("ssh-keygen") ?? throw new CliError("ssh-keygen is not installed", "install OpenSSH");
            var r = Shell.Capture(keygen, new[] { "-t", "ed25519", "-f", KeyFile, "-N", "", "-C", "nebula-orchestrator", "-q" });
            if (!r.Ok) throw new CliError($"ssh-keygen failed: {r.Output}");
        }
        return File.ReadAllText(pub).Trim();
    }

    public ShellResult Run(string ip, string command) =>
        Shell.Capture(SshExe, CommonArgs().Concat(new[] { $"root@{ip}", command }));

    public void RunOrThrow(string ip, string command)
    {
        var r = Run(ip, command);
        if (!r.Ok) throw new CliError($"ssh root@{ip}: '{command}' exited {r.ExitCode}: {r.Output}");
    }

    /// <summary>Run with the terminal attached (for log tailing).</summary>
    public int Stream(string ip, string command) =>
        Shell.Stream(SshExe, CommonArgs().Concat(new[] { $"root@{ip}", command }));

    /// <summary>Write text to a file on the VM through stdin, so secrets never appear on a command line.</summary>
    public void SendFile(string ip, string content, string remotePath, string mode = "0644")
    {
        content = content.Replace("\r\n", "\n");
        var r = Shell.Capture(SshExe, CommonArgs().Concat(new[] { $"root@{ip}", $"umask 077; cat > '{remotePath}' && chmod {mode} '{remotePath}'" }), stdin: content);
        if (!r.Ok) throw new CliError($"writing {remotePath} on {ip} failed: {r.Output}");
    }

    public void Upload(string localPath, string ip, string remotePath)
    {
        int code = Shell.Stream(ScpExe, CommonArgs().Concat(new[] { localPath, $"root@{ip}:{remotePath}" }));
        if (code != 0) throw new CliError($"scp to {ip} failed (exit {code})");
    }

    public void Wait(string ip, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var r = Shell.Capture(SshExe, CommonArgs().Concat(new[] { "-o", "BatchMode=yes", $"root@{ip}", "true" }));
            if (r.Ok) return;
            Thread.Sleep(5000);
        }
        throw new CliError($"could not reach root@{ip} over ssh within {timeoutSeconds}s");
    }

    /// <summary>Hetzner reuses public IPs, so a recreated VM shows up with a new host key.</summary>
    public void ForgetHost(string ip)
    {
        var keygen = Shell.Which("ssh-keygen");
        if (keygen != null && File.Exists(KnownHosts)) Shell.Capture(keygen, new[] { "-f", KnownHosts, "-R", ip });
    }
}
