using System.Text.Json.Nodes;

namespace Nebula.Cli.Cloud;

using Nebula.Cli.Core;

/// <summary>
/// A Nebula mesh on Hetzner Cloud: fixed resources (ssh key, private network, firewalls, the orchestrator VM) that
/// this class creates, and worker VMs the orchestrator itself creates and deletes through HetznerWorkerHost.
/// </summary>
public sealed class HetznerMesh
{
    private readonly HcloudClient _api;
    private readonly Ssh _ssh;
    private readonly CliConfig.HetznerSettings _settings;
    private readonly NebulaProject _project;

    public string MeshName => _project.File.Deploy.MeshName;
    public string NetworkName => MeshName;
    public string SshKeyName => MeshName;
    public string OrchestratorName => $"{MeshName}-orchestrator";
    public string WorkerFirewall => $"{MeshName}-worker";
    public string OrchFirewall => $"{MeshName}-orchestrator";
    public string Location => _project.File.Deploy.Location ?? _settings.Location;
    public string WorkerType => _project.File.Deploy.WorkerType ?? _settings.WorkerType;
    public string OrchestratorType => _project.File.Deploy.OrchestratorType ?? _settings.OrchestratorType;
    public string Image => _settings.Image;
    public int DashboardPort => _project.File.Mesh.DashboardPort;
    public int GatewayPort => _project.File.Mesh.GatewayPort;
    private const string NetworkRange = "10.0.0.0/16";
    private const string SubnetRange = "10.0.1.0/24";

    public HetznerMesh(CliConfig.HetznerSettings settings, NebulaProject project)
    {
        _settings = settings;
        _project = project;
        _api = new HcloudClient(settings.ResolveToken() ?? throw new CliError("Hetzner is not configured", "nebula config hetzner"));
        _ssh = new Ssh(Path.Combine(Platform.Home, ".ssh", "nebula_hetzner"));
    }

    public static string ZoneFor(string location) => location switch
    {
        "ash" => "us-east",
        "hil" => "us-west",
        "sin" => "ap-southeast",
        "nbg1" or "fsn1" or "hel1" => "eu-central",
        _ => throw new CliError($"unknown Hetzner location '{location}'", "one of ash, hil, sin, nbg1, fsn1, hel1"),
    };

    public List<JsonNode> Servers() => _api.ServersByLabel($"nebula-mesh={MeshName}");
    public JsonNode? Orchestrator() => _api.ByName("servers", OrchestratorName);

    public static string PublicIp(JsonNode server) => server["public_net"]?["ipv4"]?["ip"]?.ToString() ?? "";
    public static string PrivateIp(JsonNode server) => server["private_net"]?.AsArray().FirstOrDefault()?["ip"]?.ToString() ?? "";

    // --- provision -------------------------------------------------------------------------------------

    /// <summary>Create whatever fixed resource is missing. Idempotent.</summary>
    public JsonNode Provision()
    {
        string zone = ZoneFor(Location);
        Ui.Step($"cloud resources for mesh '{MeshName}' in {Location}");

        string pub = _ssh.EnsureKey();
        var key = _api.ByName("ssh_keys", SshKeyName);
        if (key == null)
        {
            Ui.Info($"uploading ssh key '{SshKeyName}'");
            key = _api.Post("/ssh_keys", new { name = SshKeyName, public_key = pub, labels = new Dictionary<string, string> { ["nebula-mesh"] = MeshName } })["ssh_key"]!;
        }
        else if (key["public_key"]?.ToString().Trim() != pub)
        {
            throw new CliError($"Hetzner ssh key '{SshKeyName}' does not match {_ssh.KeyFile}.pub", "delete one of them");
        }
        Ui.Ok($"ssh key {SshKeyName}");

        var net = _api.ByName("networks", NetworkName);
        if (net == null)
        {
            Ui.Info($"creating network '{NetworkName}' {NetworkRange} (subnet {SubnetRange}, zone {zone})");
            net = _api.Post("/networks", new
            {
                name = NetworkName, ip_range = NetworkRange,
                subnets = new[] { new { type = "cloud", ip_range = SubnetRange, network_zone = zone } },
                labels = new Dictionary<string, string> { ["nebula-mesh"] = MeshName },
            })["network"]!;
        }
        Ui.Ok($"network {NetworkName}");

        var anywhere = new[] { "0.0.0.0/0", "::/0" };
        var orchFw = EnsureFirewall(OrchFirewall, new object[]
        {
            new { direction = "in", protocol = "tcp", port = "22", source_ips = anywhere, description = "ssh" },
            new { direction = "in", protocol = "tcp", port = DashboardPort.ToString(), source_ips = anywhere, description = "dashboard" },
            new { direction = "in", protocol = "udp", port = GatewayPort.ToString(), source_ips = anywhere, description = "gateway (clients)" },
        });
        EnsureFirewall(WorkerFirewall, new object[]
        {
            new { direction = "in", protocol = "tcp", port = "22", source_ips = anywhere, description = "ssh" },
        });
        Ui.Ok($"firewalls {OrchFirewall}, {WorkerFirewall}");

        var orch = Orchestrator();
        if (orch == null)
        {
            Ui.Info($"creating server '{OrchestratorName}' ({OrchestratorType}, {Location}); this takes about a minute");
            var resp = _api.Post("/servers", new
            {
                name = OrchestratorName, server_type = OrchestratorType, image = Image, location = Location,
                ssh_keys = new[] { key["id"]!.GetValue<long>() },
                networks = new[] { net["id"]!.GetValue<long>() },
                firewalls = new[] { new { firewall = orchFw["id"]!.GetValue<long>() } },
                user_data = "#!/bin/bash\nmkdir -p /opt/nebula/bin /opt/nebula/artifacts /etc/nebula /var/log/nebula\n",
                start_after_create = true,
                public_net = new { enable_ipv4 = true, enable_ipv6 = false },
                labels = new Dictionary<string, string> { ["nebula-mesh"] = MeshName, ["nebula-role"] = "orchestrator" },
            });
            _api.WaitAction(resp["action"]);
            orch = resp["server"]!;
            string ip = PublicIp(orch);
            _ssh.ForgetHost(ip);
            Ui.Info("waiting for the VM to accept ssh...");
            _ssh.Wait(ip, 240);
            // Re-read: the private IP is assigned after creation.
            orch = Orchestrator() ?? orch;
        }
        Ui.Ok($"orchestrator VM {OrchestratorName} public {PublicIp(orch)} private {PrivateIp(orch)}");
        return orch;
    }

    private JsonNode EnsureFirewall(string name, object[] rules)
    {
        var fw = _api.ByName("firewalls", name);
        if (fw != null) return fw;
        Ui.Info($"creating firewall '{name}'");
        return _api.Post("/firewalls", new { name, rules, labels = new Dictionary<string, string> { ["nebula-mesh"] = MeshName } })["firewall"]!;
    }

    // --- deploy ------------------------------------------------------------------------------------------

    public sealed record DeployOptions(int Workers, int Npcs, string SpacetimeUri, string Database, bool SkipUpload, bool Verbose);

    /// <summary>Ship the tarball and (re)start the orchestrator service. Returns the dashboard URL.</summary>
    public string Deploy(JsonNode orch, string tarball, DeployOptions o)
    {
        string publicIp = PublicIp(orch);
        string privateIp = PrivateIp(orch);
        if (string.IsNullOrEmpty(privateIp)) throw new CliError($"{OrchestratorName} has no private network address", "re-run `nebula deploy`; provisioning attaches the network");
        string location = orch["location"]?["name"]?.ToString() ?? Location;

        Ui.Step($"deploying to {OrchestratorName} ({publicIp})");
        _ssh.Wait(publicIp, 60);
        _ssh.RunOrThrow(publicIp, "mkdir -p /opt/nebula/bin /opt/nebula/artifacts /etc/nebula /var/log/nebula");

        if (!o.SkipUpload)
        {
            if (!File.Exists(tarball)) throw new CliError($"no build tarball at {tarball}", "run `nebula build --linux`");
            Ui.Info($"uploading {Path.GetFileName(tarball)} ({new FileInfo(tarball).Length / 1024 / 1024} MB)");
            _ssh.Upload(tarball, publicIp, "/opt/nebula/artifacts/nebula-linux.tar.gz.uploading");
            // Stop the mesh before swapping binaries: the orchestrator deletes its worker VMs on shutdown.
            _ssh.RunOrThrow(publicIp,
                "systemctl stop nebula-orchestrator 2>/dev/null; " +
                "mv /opt/nebula/artifacts/nebula-linux.tar.gz.uploading /opt/nebula/artifacts/nebula-linux.tar.gz && " +
                "rm -rf /opt/nebula/bin && mkdir -p /opt/nebula/bin && tar xzf /opt/nebula/artifacts/nebula-linux.tar.gz -C /opt/nebula/bin && " +
                $"chmod +x /opt/nebula/bin/{_project.File.Executable}.x86_64");
            Ui.Ok("build installed");
        }

        // The provider token goes over stdin into a root-only file, never onto a command line.
        _ssh.SendFile(publicIp, $"HCLOUD_TOKEN={_settings.ResolveToken()}\n", "/etc/nebula/env", "0600");

        var args = new List<string>
        {
            "-batchmode", "-nographics",
            "-nebula-role", "orchestrator",
            "-nebula-host", "hetzner",
            "-nebula-workers", o.Workers.ToString(),
            "-nebula-settings", $"npcs={o.Npcs}",
            "-nebula-dashboard-port", DashboardPort.ToString(),
            "-nebula-dashboard-bind", "+",
            "-nebula-build-dir", "/opt/nebula/artifacts",
            "-nebula-advertise", privateIp,
            "-nebula-gateway", $"{publicIp}:{GatewayPort}",
            "-nebula-spacetime", o.SpacetimeUri,
            "-nebula-database", o.Database,
            "-nebula-cloud-mesh", MeshName,
            "-nebula-cloud-location", location,
            "-nebula-cloud-type", WorkerType,
            "-nebula-cloud-image", Image,
            "-nebula-cloud-network", NetworkName,
            "-nebula-cloud-sshkey", SshKeyName,
            "-nebula-cloud-firewall", WorkerFirewall,
            "-logFile", "/var/log/nebula/orchestrator.log",
        };
        if (o.Verbose) args.Add("-nebula-verbose");
        string unit = $@"[Unit]
Description=Nebula orchestrator (Nebula Dashboard + gateway + Hetzner worker host)
After=network-online.target
Wants=network-online.target

[Service]
WorkingDirectory=/opt/nebula/bin
EnvironmentFile=/etc/nebula/env
ExecStart=/opt/nebula/bin/{_project.File.Executable}.x86_64 {string.Join(" ", args)}
Restart=on-failure
RestartSec=5
KillMode=mixed
TimeoutStopSec=30
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
";
        _ssh.SendFile(publicIp, unit, "/etc/systemd/system/nebula-orchestrator.service");
        _ssh.RunOrThrow(publicIp, "systemctl daemon-reload && systemctl enable nebula-orchestrator >/dev/null 2>&1; systemctl restart nebula-orchestrator");
        Ui.Ok("orchestrator service restarted");

        string url = $"http://{publicIp}:{DashboardPort}/";
        Ui.Info("waiting for the dashboard...");
        var state = LocalMesh.WaitForDashboard(url, 80);
        if (state == null)
        {
            Ui.Warn("the orchestrator did not answer within 80s; last log lines:");
            _ssh.Stream(publicIp, "journalctl -u nebula-orchestrator -n 20 --no-pager; tail -n 30 /var/log/nebula/orchestrator.log 2>/dev/null");
            throw new CliError("deploy did not come up", "nebula logs --cloud orchestrator");
        }
        Ui.Ok($"mesh is up: {LocalMesh.Summary(state)} (worker VMs take ~30s each to register)");
        return url;
    }

    // --- status / logs / destroy -------------------------------------------------------------------------

    public void PrintServers(List<JsonNode> servers)
    {
        Ui.Info($"Hetzner servers labelled nebula-mesh={MeshName}:");
        Ui.Table(new[] { "name", "role", "worker", "status", "type", "public", "private", "created" },
            servers.OrderBy(s => s["name"]?.ToString()).Select(s => new[]
            {
                s["name"]?.ToString() ?? "", s["labels"]?["nebula-role"]?.ToString() ?? "", s["labels"]?["nebula-worker"]?.ToString() ?? "",
                s["status"]?.ToString() ?? "", s["server_type"]?["name"]?.ToString() ?? "", PublicIp(s), PrivateIp(s),
                s["created"]?.ToString() is { Length: >= 16 } c ? c.Substring(0, 16).Replace('T', ' ') : "",
            }));
    }

    public int Logs(string what, int lines)
    {
        var servers = Servers();
        var orch = servers.FirstOrDefault(s => s["name"]?.ToString() == OrchestratorName) ?? throw new CliError("no orchestrator server", "nebula deploy");
        switch (what)
        {
            case "orchestrator":
                return _ssh.Stream(PublicIp(orch), $"journalctl -u nebula-orchestrator -n 10 --no-pager; tail -n {lines} /var/log/nebula/orchestrator.log");
            case "gateway":
                return _ssh.Stream(PublicIp(orch), $"tail -n {lines} /opt/nebula/bin/Logs/gateway.log");
            default:
                var w = servers.FirstOrDefault(s => s["labels"]?["nebula-worker"]?.ToString() == what)
                    ?? throw new CliError($"no server for worker '{what}'", "servers: " + string.Join(", ", servers.Select(s => s["name"])));
                string ip = PublicIp(w);
                _ssh.ForgetHost(ip);
                return _ssh.Stream(ip, $"tail -n 5 /var/log/nebula-bootstrap.log; echo ---; tail -n {lines} /var/log/nebula-worker.log");
        }
    }

    public void Destroy(bool all)
    {
        var servers = Servers();
        if (servers.Count == 0) Ui.Info("no mesh servers");
        foreach (var s in servers)
        {
            Ui.Info($"deleting server {s["name"]} ({PublicIp(s)})");
            _api.Delete($"/servers/{s["id"]}");
        }
        if (!all) return;
        Thread.Sleep(5000); // server deletion detaches from network/firewalls asynchronously
        foreach (var name in new[] { OrchFirewall, WorkerFirewall })
        {
            var fw = _api.ByName("firewalls", name);
            if (fw != null) { Ui.Info($"deleting firewall {name}"); _api.Delete($"/firewalls/{fw["id"]}"); }
        }
        var net = _api.ByName("networks", NetworkName);
        if (net != null) { Ui.Info($"deleting network {NetworkName}"); _api.Delete($"/networks/{net["id"]}"); }
        var key = _api.ByName("ssh_keys", SshKeyName);
        if (key != null) { Ui.Info($"deleting ssh key {SshKeyName}"); _api.Delete($"/ssh_keys/{key["id"]}"); }
    }
}
