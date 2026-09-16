using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nebula.Cli.Cloud;

using Nebula.Cli.Commands;
using Nebula.Cli.Core;

/// <summary>Follows a Nebula Cloud operation (rollout, rollback, destroy, ...) to its end, printing its steps and events.</summary>
public static class OperationFollower
{
    /// <summary>The wait= of the events long poll, in seconds.</summary>
    internal static int PollSeconds = 30;

    /// <summary>
    /// Print events until the operation finishes. Returns the final operation; throws a CliError when it failed. Ctrl-C
    /// (or <paramref name="ct"/>) leaves the operation running and says how to reattach.
    /// </summary>
    public static CloudApi.Operation Follow(CloudApi api, CloudApi.Operation op, CancellationToken ct = default)
    {
        CancellationTokenSource? cts = null;
        ConsoleCancelEventHandler? handler = null;
        if (ct == default)
        {
            cts = new CancellationTokenSource();
            ct = cts.Token;
            handler = (_, e) => { e.Cancel = true; cts.Cancel(); };
            Console.CancelKeyPress += handler;
        }
        try
        {
            return FollowCore(api, op, ct);
        }
        catch (OperationCanceledException)
        {
            Ui.Warn($"operation {op.Id} ({op.Kind}) is still running; re-run the command to reattach");
            throw;
        }
        finally
        {
            if (handler != null) Console.CancelKeyPress -= handler;
            cts?.Dispose();
        }
    }

    private static CloudApi.Operation FollowCore(CloudApi api, CloudApi.Operation op, CancellationToken ct)
    {
        Ui.Step($"{op.Kind} {op.Id} ({op.State})");
        var steps = new Dictionary<string, string>();
        PrintSteps(op, steps);
        long after = 0;
        var started = DateTime.UtcNow;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = api.Events(op.Id, after, PollSeconds, ct);
            foreach (var e in page.Events ?? new List<CloudApi.OperationEvent>())
            {
                after = Math.Max(after, e.Seq);
                string time = e.At is { Length: >= 19 } t ? t.Substring(11, 8) : "";
                string text = $"{time} {(e.Step != null ? "[" + e.Step + "] " : "")}{e.Message}";
                if (e.Level is "error") Ui.Warn(text);
                else if (e.Level is "warn") Ui.Warn(text);
                else Ui.Info(text);
            }
            op = page.Operation;
            PrintSteps(op, steps);
            if (op.IsFinished) break;
        }
        double seconds = (DateTime.UtcNow - started).TotalSeconds;
        switch (op.State)
        {
            case "succeeded":
                Ui.Ok($"{op.Kind} succeeded ({seconds:F0}s)");
                return op;
            case "cancelled":
                throw new CliError($"{op.Kind} {op.Id} was cancelled");
            default:
                var failed = op.Steps?.FirstOrDefault(s => s.State == "failed");
                string why = op.Error?.Message ?? failed?.Message ?? "no error message";
                throw new CliError($"{op.Kind} {op.Id} failed{(failed != null ? " at " + failed.Name : "")}: {why}", "nebula logs --cloud orchestrator");
        }
    }

    private static void PrintSteps(CloudApi.Operation op, Dictionary<string, string> seen)
    {
        foreach (var s in op.Steps ?? new List<CloudApi.OperationStep>())
        {
            if (seen.TryGetValue(s.Name, out var prev) && prev == s.State) continue;
            seen[s.Name] = s.State;
            switch (s.State)
            {
                case "running": Ui.Info($"step {s.Name}..."); break;
                case "succeeded": Ui.Ok($"step {s.Name}{(s.Message != null ? ": " + s.Message : "")}"); break;
                case "failed": Ui.Warn($"step {s.Name} failed{(s.Message != null ? ": " + s.Message : "")}"); break;
            }
        }
    }

    /// <summary>A pending or running operation on the deployment, if any: re-running a command reattaches to it.</summary>
    public static CloudApi.Operation? FindRunning(CloudApi api, string deploymentId) =>
        api.Operations(deploymentId).FirstOrDefault(o => o.IsActive);
}

/// <summary>`nebula deploy --target cloud`: build, upload, release, roll out.</summary>
public static class CloudDeploy
{
    public sealed record Options(string? ReleaseId, bool SkipBuild, bool AllowProtocolChange, string? Label, int? MinWorkers, int? MaxWorkers, string? WorkerSize, bool OpenUi);

    public static int Run(Context ctx, NebulaProject project, ParsedArgs args, Options o)
    {
        var api = CloudApi.Require(ctx);
        var band = args.Has("workers") || args.Has("min") || args.Has("max")
            ? WorkerBand.Resolve(args, project.File.Deploy.Workers, project.File.Deploy.MinWorkers, project.File.Deploy.MaxWorkers)
            : (WorkerBand?)null;
        var target = CloudTarget.Resolve(ctx, api, project, args, create: true,
            new CloudTarget.CreateDefaults(args.Get("region"), o.WorkerSize, band?.Min ?? o.MinWorkers, band?.Max ?? o.MaxWorkers));
        var dep = target.Deployment;
        Ui.Title($"deploying {Path.GetFileName(project.Root)} to Nebula Cloud: {target.Describe} ({dep.Region}, {dep.State})");

        // --- reattach ------------------------------------------------------------------------------------
        var running = OperationFollower.FindRunning(api, dep.Id);
        if (running != null)
        {
            Ui.Warn($"operation {running.Id} ({running.Kind}) is already {running.State} on {dep.Name}; following it instead of starting another");
            OperationFollower.Follow(api, running);
            return Finish(api, dep.Id, project, o.OpenUi);
        }

        // --- release ------------------------------------------------------------------------------------
        CloudApi.Release release;
        if (o.ReleaseId != null)
        {
            release = api.GetRelease(o.ReleaseId);
            if (release.ProjectId != target.Project.Id) throw new CliError($"release {release.Id} belongs to another project");
            Ui.Info($"release #{release.Number} {release.Id} ({release.Label ?? "no label"}, nebula {release.NebulaVersion}, protocol {release.ProtocolVersion?.ToString() ?? "?"})");
        }
        else
        {
            if (!o.SkipBuild) UnityBuild.Build(ctx, project, new UnityBuild.Options(BuildTarget.Linux));
            else if (!File.Exists(project.LinuxTarball)) throw new CliError($"no {project.LinuxTarball}", "drop --skip-build, or run `nebula build --linux`");
            release = CreateRelease(ctx, api, project, target.Project.Id, o.Label);
        }

        // --- deployment settings ----------------------------------------------------------------------------
        var patch = new Dictionary<string, object>();
        int? min = band?.Min ?? o.MinWorkers, max = band?.Max ?? o.MaxWorkers;
        if (min != null && min != dep.MinWorkers) patch["minWorkers"] = min;
        if (max != null && max != dep.MaxWorkers) patch["maxWorkers"] = max;
        if (o.WorkerSize != null && o.WorkerSize != dep.WorkerSize) patch["workerSize"] = o.WorkerSize;
        if (args.Get("region") is { } region && region != dep.Region)
            Ui.Warn($"deployment {dep.Name} is in {dep.Region}; a region cannot change after creation (--region only applies to a new deployment)");
        if (patch.Count > 0)
        {
            Ui.Step("updating the deployment: " + string.Join(", ", patch.Select(kv => $"{kv.Key}={kv.Value}")));
            dep = api.PatchDeployment(dep.Id, patch);
        }

        // --- rollout ----------------------------------------------------------------------------------------
        Ui.Step($"rolling out release #{release.Number} to {dep.Name}");
        CloudApi.Operation op;
        try
        {
            op = api.Rollout(dep.Id, release.Id, o.AllowProtocolChange);
        }
        catch (CloudApiError e) when (CloudApi.RunningOperationOf(e) is { } other)
        {
            Ui.Warn($"operation {other.Id} ({other.Kind}) is already running on {dep.Name}; following it");
            op = other.Kind.Length > 0 && other.Steps != null ? other : api.GetOperation(other.Id);
        }
        catch (CloudApiError e) when (e.Status == 409 && e.Code == "conflict" && e.Message.Contains("protocol", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliError(e.Message, "the wire protocol version changed and connected players would be disconnected; re-run with --allow-protocol-change");
        }
        OperationFollower.Follow(api, op);
        return Finish(api, dep.Id, project, o.OpenUi);
    }

    private static int Finish(CloudApi api, string deploymentId, NebulaProject project, bool openUi)
    {
        var dep = api.GetDeployment(deploymentId);
        Ui.Blank();
        Ui.Ok($"deployed: {dep.Name} is {dep.State}" + (dep.CurrentReleaseId != null ? $" on release {dep.CurrentReleaseId}" : ""));
        if (dep.GatewayAddress != null) Ui.Info($"gateway    {dep.GatewayAddress}   (client: {project.File.Executable} -nebula-role client -nebula-gateway {dep.GatewayAddress})");
        if (dep.WebUrl != null) Ui.Info($"web        {dep.WebUrl}");
        if (dep.DashboardUrl != null) Ui.Info($"dashboard  {dep.DashboardUrl}");
        Ui.Info("status     nebula status --cloud     logs: nebula logs --cloud orchestrator|gateway|worker --follow");
        Ui.Info("scale      nebula scale --min N --max N     roll back: nebula rollback     tear down: nebula destroy");
        if (openUi && dep.DashboardUrl != null) Platform.OpenBrowser(dep.DashboardUrl);
        return 0;
    }

    /// <summary>Upload Builds/nebula-linux.tar.gz (unless the project already has it) and create an immutable release from it.</summary>
    public static CloudApi.Release CreateRelease(Context ctx, CloudApi api, NebulaProject project, string projectId, string? label)
    {
        string tarball = project.LinuxTarball;
        string manifestPath = Path.Combine(project.LinuxBuildDir, ServiceBuild.ManifestName);
        if (!File.Exists(manifestPath)) throw new CliError($"no service manifest at {manifestPath}", "run `nebula build --linux` to export the game's configuration and containers");
        JsonNode manifest;
        try { manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) ?? new JsonObject(); }
        catch (JsonException e) { throw new CliError($"{manifestPath} is not valid JSON: {e.Message}", "run `nebula build --linux` again"); }

        Ui.Step($"uploading {Path.GetFileName(tarball)}");
        long size = new FileInfo(tarball).Length;
        string sha = Sha256(tarball);
        Ui.Info($"{size / 1024.0 / 1024.0:F1} MB, sha256 {sha.Substring(0, 12)}...");
        var created = api.CreateArtifact(projectId, sha, size, Path.GetFileName(tarball));
        var artifact = created.Artifact;
        if (created.Upload == null && artifact.State == "verified")
        {
            Ui.Ok($"artifact {artifact.Id} already uploaded (same content)");
        }
        else
        {
            if (created.Upload == null) throw new CliError($"artifact {artifact.Id} is {artifact.State} and the API offered no upload");
            // A deploy interrupted after its upload left the bytes in storage: verify those before sending them again.
            artifact = VerifyExisting(api, projectId, artifact);
            if (artifact.State != "verified")
            {
                var progress = new UploadProgress(size);
                api.Upload(created.Upload, tarball, progress.Report);
                progress.Done();
                artifact = WaitForVerification(api, projectId, api.CompleteArtifact(projectId, artifact.Id));
                if (artifact.State != "verified")
                    throw new CliError($"the upload of artifact {artifact.Id} did not verify ({artifact.Error ?? artifact.State})", "run `nebula deploy` again");
            }
            Ui.Ok($"artifact {artifact.Id} verified");
        }

        int? protocol = ReadProtocolVersion(project);
        var git = GitInfo(project.Root);
        var release = api.CreateRelease(projectId, artifact.Id, manifest, label, null, Platform.CliVersion, protocol, git);
        Ui.Ok($"release #{release.Number} {release.Id} (nebula {release.NebulaVersion ?? Platform.CliVersion}, protocol {protocol?.ToString() ?? "unknown"}{(git?.Commit != null ? $", {git.Branch}@{git.Commit.Substring(0, Math.Min(8, git.Commit.Length))}{(git.Dirty ? " dirty" : "")}" : "")})");
        return release;
    }

    /// <summary>
    /// Ask the API to verify whatever storage holds for the artifact. A 400 means nothing usable is there (not
    /// uploaded, or the wrong size) and the caller uploads; otherwise the verification is followed to its end and a
    /// "failed" result (corrupt bytes) also makes the caller upload.
    /// </summary>
    private static CloudApi.Artifact VerifyExisting(CloudApi api, string projectId, CloudApi.Artifact artifact)
    {
        CloudApi.Artifact started;
        try { started = api.CompleteArtifact(projectId, artifact.Id); }
        catch (CloudApiError e) when (e.Status == 400) { return artifact; }
        if (started.State == "verified") { Ui.Ok($"artifact {artifact.Id} already uploaded (same content)"); return started; }
        Ui.Info("an earlier upload of these bytes is in storage; verifying it instead of uploading again");
        return WaitForVerification(api, projectId, started);
    }

    /// <summary>
    /// The API reads the whole object back from storage through SHA-256 in the background (minutes for a game
    /// build); poll until it settles. Ctrl-C is safe: the verification keeps running and the next deploy resumes.
    /// </summary>
    private static CloudApi.Artifact WaitForVerification(CloudApi api, string projectId, CloudApi.Artifact artifact)
    {
        if (artifact.State is not ("verifying" or "pending")) return artifact;
        Ui.Info($"verifying on the server: the API reads the {artifact.SizeBytes / 1024.0 / 1024.0:F0} MB back from storage (a few minutes)");
        var started = DateTime.UtcNow;
        var lastNote = started;
        while (artifact.State is "verifying" or "pending")
        {
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(30))
                throw new CliError($"artifact {artifact.Id} is still {artifact.State} after 30 minutes", "run `nebula deploy` again; it resumes from the uploaded bytes");
            Thread.Sleep(TimeSpan.FromSeconds(3));
            artifact = api.GetArtifact(projectId, artifact.Id);
            if (DateTime.UtcNow - lastNote >= TimeSpan.FromSeconds(30))
            {
                Ui.Info($"still verifying ({(DateTime.UtcNow - started).TotalSeconds:F0} s)");
                lastNote = DateTime.UtcNow;
            }
        }
        return artifact;
    }

    private sealed class UploadProgress
    {
        private readonly long _total;
        private readonly bool _live = !Console.IsOutputRedirected;
        private int _lastPercent = -1;
        private DateTime _lastPrint = DateTime.MinValue;
        public UploadProgress(long total) { _total = total; }

        public void Report(long sent, long total)
        {
            int percent = total > 0 ? (int)(sent * 100 / total) : 100;
            if (_live)
            {
                if ((DateTime.UtcNow - _lastPrint).TotalMilliseconds < 200 && sent < total) return;
                _lastPrint = DateTime.UtcNow;
                Console.Write($"\r    uploading {sent / 1024.0 / 1024.0:F1} / {total / 1024.0 / 1024.0:F1} MB ({percent}%)   ");
            }
            else if (percent / 10 != _lastPercent / 10)
            {
                Ui.Info($"uploading {percent}%");
            }
            _lastPercent = percent;
        }

        public void Done()
        {
            if (_live) Console.Write("\r" + new string(' ', 60) + "\r");
            Ui.Info($"uploaded {_total / 1024.0 / 1024.0:F1} MB");
        }
    }

    public static string Sha256(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>HelloMsg.ProtocolVersion from the package's Runtime/Protocol/Messages.cs, or null when the package is not resolved.</summary>
    public static int? ReadProtocolVersion(NebulaProject project)
    {
        string? dir = project.FindPackageDir();
        if (dir == null) return null;
        string file = Path.Combine(dir, "Runtime", "Protocol", "Messages.cs");
        if (!File.Exists(file)) return null;
        var m = Regex.Match(File.ReadAllText(file), @"ProtocolVersion\s*=\s*(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : null;
    }

    /// <summary>Commit, branch and dirty flag of the project's checkout; null when git or the repository is missing.</summary>
    public static CloudApi.GitInfo? GitInfo(string root)
    {
        string? git = Shell.Which("git");
        if (git == null) return null;
        var head = Shell.Capture(git, new[] { "rev-parse", "HEAD" }, root);
        if (!head.Ok) return null;
        var branch = Shell.Capture(git, new[] { "rev-parse", "--abbrev-ref", "HEAD" }, root);
        var status = Shell.Capture(git, new[] { "status", "--porcelain" }, root);
        return new CloudApi.GitInfo(head.Stdout.Trim(), branch.Ok ? branch.Stdout.Trim() : null, status.Ok && status.Stdout.Trim().Length > 0);
    }
}

/// <summary>`nebula status --cloud` for a Nebula Cloud deployment.</summary>
public static class CloudStatus
{
    public static void Print(CloudApi.DeploymentStatus s, string executable)
    {
        var d = s.Deployment;
        Ui.Info($"deployment {d.Name} ({d.Id})  state={d.State} health={s.Health ?? "unknown"} region={d.Region} workers={d.WorkerSize} {d.MinWorkers}..{d.MaxWorkers} gateways={d.GatewaySize} {d.MinGateways}..{d.MaxGateways}{(d.SpendLimited ? "  SPEND LIMIT REACHED" : "")}");
        if (s.Release != null) Ui.Info($"release    #{s.Release.Number} {s.Release.Id}{(s.Release.Label != null ? " " + s.Release.Label : "")}  nebula {s.Release.NebulaVersion} protocol {s.Release.ProtocolVersion?.ToString() ?? "?"}{(s.Release.Git?.Commit is { Length: > 0 } c ? $"  {s.Release.Git.Branch}@{c.Substring(0, Math.Min(8, c.Length))}" : "")}");
        else Ui.Info("release    none (run `nebula deploy`)");
        if (d.GatewayAddress != null) Ui.Info($"gateway    {d.GatewayAddress}   (client: {executable} -nebula-role client -nebula-gateway {d.GatewayAddress})");
        if (d.WebUrl != null) Ui.Info($"web        {d.WebUrl}");
        if (d.DashboardUrl != null) Ui.Info($"dashboard  {d.DashboardUrl}");
        if (s.Orchestrator is { } o)
            Ui.Info($"orchestrator {o.State ?? "?"}  address {o.Address ?? "-"}{(o.PublicIp != null ? " public " + o.PublicIp : "")}  version {o.Version ?? "?"}  heartbeat {Seconds(o.HeartbeatAgeSeconds)}");
        if (s.LoadBalancer is { } lb)
            Ui.Info($"load balancer {lb.State ?? "?"}  ip {lb.Ip ?? "-"}  healthy gateways {lb.HealthyGateways?.ToString() ?? "?"}");
        if (s.Mesh is { } m)
            Ui.Info($"mesh       desired {m.DesiredWorkers ?? 0} live {m.LiveWorkers ?? 0} worker(s), {m.Players ?? 0} player(s), {m.Bots ?? 0} bot(s), {m.PendingJoins ?? 0} pending join(s){(m.Npcs != null ? $", {m.Npcs} NPC(s)" : "")}");
        if (s.Mesh?.PendingJoins > 0 && (s.Mesh.LiveWorkers ?? 0) == 0)
            Ui.Warn($"world starting: {s.Mesh.PendingJoins} client(s) waiting for a worker to boot");
        if (s.SampledAt != null) Ui.Info($"sampled    {s.SampledAt}");

        Ui.Blank();
        Ui.Info("gateways:");
        Ui.Table(new[] { "gateway", "state", "lb", "address", "clients", "joining", "reconn", "pps in/out", "bytes/s in/out", "to workers", "cpu", "lag ms", "workers", "hb s" },
            (s.Gateways ?? new List<CloudApi.GatewayStatus>()).Select(g => new[]
            {
                g.Id + (g.Incarnation != null ? "#" + g.Incarnation : ""),
                g.State ?? "",
                g.InLoadBalancer == true ? "yes" : "no",
                g.Address ?? "",
                g.Clients?.Active?.ToString() ?? "0", g.Clients?.Joining?.ToString() ?? "0", g.Clients?.Reconnecting?.ToString() ?? "0",
                $"{Num(g.PacketsPerSecond?.In)}/{Num(g.PacketsPerSecond?.Out)}",
                $"{Bytes(g.BytesPerSecond?.In)}/{Bytes(g.BytesPerSecond?.Out)}",
                Bytes(g.BytesPerSecond?.Workers),
                g.Cpu is { } cpu ? $"{cpu * (cpu <= 1 ? 100 : 1):F0}%" : "",
                g.LoopLagMs is { } lag ? lag.ToString("F1") : "",
                g.WorkerConnections?.ToString() ?? "",
                Seconds(g.HeartbeatAgeSeconds),
            }));
        Ui.Blank();
        Ui.Info("workers:");
        Ui.Table(new[] { "worker", "index", "size", "state", "address", "tick ms", "util", "entities", "players", "bots", "hb s" },
            (s.Workers ?? new List<CloudApi.WorkerStatus>()).OrderBy(w => w.Index ?? 0).Select(w => new[]
            {
                w.Id, w.Index?.ToString() ?? "", w.Size ?? "", w.State ?? "", w.PrivateAddress ?? "",
                w.TickMs is { } t ? t.ToString("F1") : "",
                w.Utilization is { } u ? $"{u * (u <= 1 ? 100 : 1):F0}%" : "",
                w.Entities?.ToString() ?? "", w.Players?.ToString() ?? "0", w.Bots?.ToString() ?? "0",
                Seconds(w.HeartbeatAgeSeconds),
            }));
    }

    private static string Seconds(double? s) => s is { } v ? v.ToString("F1") : "";
    private static string Num(double? v) => v is { } n ? n.ToString("F0") : "-";
    private static string Bytes(double? v)
    {
        if (v is not { } b) return "-";
        if (b >= 1024 * 1024) return $"{b / 1024 / 1024:F1}M";
        if (b >= 1024) return $"{b / 1024:F1}K";
        return b.ToString("F0");
    }
}
