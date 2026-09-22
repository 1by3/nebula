using System.Globalization;
using System.Text;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The thresholds the scale and failure suite asserts, in one place, so the table in <c>docs/scale-suite.md</c>
/// and the code cannot drift apart. Every value is justified in that document; the ones marked
/// <b>provisional</b> there are the ones no line of production code fixes, and they are named as such here too.
/// </summary>
public static class ScaleThresholds
{
    /// <summary>
    /// Bytes per second one client may be sent while every entity it can see is moving. Provisional: it is a
    /// property of this fixture's world (how many entities sit inside one client's interest radius), not of a
    /// production budget. It exists to catch an interest regression that starts sending a client the world.
    /// </summary>
    public const double BytesPerClientPerSecond = 512 * 1024;

    /// <summary>
    /// Wall-clock seconds from an abrupt gateway stop to every one of its clients holding its old session again
    /// on another gateway. Provisional; measured, not derived. The derived part is what it must stay under:
    /// <c>GatewaySessionAdmission</c>'s 10 s coordination deadline, or the reclaim is a refusal, not a reclaim.
    /// </summary>
    public const double GatewayReclaimSeconds = 10.0;

    /// <summary>
    /// Wall-clock seconds from a worker's death to every orphaned container being owned again and every client
    /// holding its replicas again. Per container, so a mesh scales the budget with its container count.
    /// Provisional: the restore here is an in-process re-spawn, not a Unity scene load.
    /// </summary>
    public const double RestoreSecondsPerContainer = 2.0;

    /// <summary>
    /// Wall-clock seconds a control-plane restart may leave the mesh without lease rows. Provisional. What is
    /// <i>not</i> provisional is the assertion beside it: no client is disconnected and no session is lost while
    /// the control plane is away, because a gateway's client links do not depend on it.
    /// </summary>
    public const double ControlPlaneStallSeconds = 5.0;

    /// <summary>
    /// Wall-clock seconds from an orchestrator process going down to every worker and gateway mirroring its
    /// leases again from a replacement on the same storage. <b>Derived</b>, and the derivation is the reason the
    /// decision in <c>docs/control-plane-availability.md</c> D3 is "fast restart, not hot standby":
    /// <see cref="RemoteControlPlane.DisconnectAfterSeconds"/> is 15 s, so a restart inside this budget is one no
    /// mirror even reports as a disconnection. A restart slower than 15 s is still survived — writes are queued,
    /// not dropped — but it is no longer invisible.
    /// </summary>
    public const double OrchestratorRestartSeconds = 15.0;

    /// <summary>
    /// Wall-clock seconds the control plane's <i>store</i> may be unreachable across a database failover before
    /// the mesh is called broken. <b>Provisional</b>, and deliberately loose: what it bounds is a container
    /// restart on a development machine, and a managed PostgreSQL failover is usually slower. The assertion that
    /// matters beside it is not the number — it is that nothing was lost and nothing threw out of
    /// <c>ControlPlaneHost.Tick</c> while the database was gone.
    /// </summary>
    public const double DatabaseFailoverSeconds = 60.0;
}

/// <summary>
/// A restore curve, compared against the one checked in under <c>docs/baselines/</c>. The baseline is a shape,
/// not a stopwatch reading: absolute times depend on the machine, so what is asserted is that the curve is
/// monotonic, that it has the steps the baseline has, and that each step stays inside the per-container budget
/// and inside a generous multiple of the baseline step. A run on a machine with no baseline writes one and says
/// so rather than failing (docs/scale-suite.md, D5).
/// </summary>
public static class ScaleBaseline
{
    /// <summary>How much slower than the checked-in baseline a step may be before it is called a regression.</summary>
    public const double SlackFactor = 6.0;

    public static void CompareCurve(string name, IReadOnlyList<(string Container, double Seconds)> curve, double perContainerBudget)
    {
        Assert.That(curve, Is.Not.Empty, "an empty restore curve proves nothing");
        for (int i = 1; i < curve.Count; i++)
            Assert.That(curve[i].Seconds, Is.GreaterThanOrEqualTo(curve[i - 1].Seconds), "the curve must be monotonic in time");
        for (int i = 0; i < curve.Count; i++)
        {
            double step = curve[i].Seconds - (i == 0 ? 0 : curve[i - 1].Seconds);
            Assert.That(step, Is.LessThan(perContainerBudget),
                $"{curve[i].Container} took {step:0.00} s to come back, over the {perContainerBudget:0.00} s per-container budget");
        }

        string path = ScaleReport.BaselinePath(name);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, new[] { "step,container,seconds" }
                .Concat(curve.Select((p, i) => $"{i + 1},{p.Container},{p.Seconds.ToString("0.###", CultureInfo.InvariantCulture)}")));
            TestContext.Out.WriteLine($"[scale:{ScaleReport.Layer}] {name}: no baseline; wrote one to {path}");
            return;
        }

        var baseline = File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0)
            .Select(l => l.Split(',')).Select(f => (Container: f[1], Seconds: double.Parse(f[2], CultureInfo.InvariantCulture))).ToList();
        Assert.That(curve.Select(p => p.Container), Is.EqualTo(baseline.Select(b => b.Container)).AsCollection,
            $"the restore visited different containers than {path} records");
        for (int i = 0; i < curve.Count; i++)
        {
            double step = curve[i].Seconds - (i == 0 ? 0 : curve[i - 1].Seconds);
            double was = baseline[i].Seconds - (i == 0 ? 0 : baseline[i - 1].Seconds);
            Assert.That(step, Is.LessThanOrEqualTo(Math.Max(was, 0.05) * SlackFactor),
                $"{curve[i].Container} came back in {step:0.00} s against a baseline of {was:0.00} s in {path}");
        }
    }
}

/// <summary>
/// Where a scale run's numbers go. Every artifact says which layer produced it — <c>synthetic</c> for the
/// in-process runs in this assembly, <c>unity</c> for a real player-build mesh driven by
/// <c>Tools/scale-suite.ps1</c> — because the two measure different things and a CSV that does not say which it
/// is is worse than no CSV (docs/scale-suite.md, D1a).
/// <para>
/// Files land under <c>Logs/scale/</c> at the repository root, next to the conformance runner's output. One file
/// per scenario, overwritten each run; the fixture prints the path so a failing run says where to look.
/// </para>
/// </summary>
public sealed class ScaleReport
{
    public const string Layer = "synthetic";

    private readonly string _scenario;
    private readonly StringBuilder _rows = new();
    private readonly List<string> _notes = new();

    public ScaleReport(string scenario, params string[] columns)
    {
        _scenario = scenario;
        _rows.Append("layer,scenario,").AppendLine(string.Join(",", columns));
    }

    /// <summary>One measurement. Doubles are written invariant with three decimals; everything else verbatim.</summary>
    public void Row(params object[] values)
    {
        _rows.Append(Layer).Append(',').Append(_scenario);
        foreach (var v in values)
        {
            _rows.Append(',');
            _rows.Append(v switch
            {
                double d => d.ToString("0.###", CultureInfo.InvariantCulture),
                float f => f.ToString("0.###", CultureInfo.InvariantCulture),
                null => "",
                _ => v.ToString()!.Replace(",", ";"),
            });
        }
        _rows.AppendLine();
    }

    /// <summary>A sentence for the run log and the test output: what this scenario concluded.</summary>
    public void Note(string note) => _notes.Add(note);

    /// <summary>Write the CSV and echo the notes. Safe to call from a teardown that a failing test never reached.</summary>
    public void Write()
    {
        string directory = Path.Combine(RepositoryRoot(), "Logs", "scale");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Layer + "-" + _scenario + ".csv");
        File.WriteAllText(path, _rows.ToString());
        TestContext.Out.WriteLine($"[scale:{Layer}] {_scenario} -> {path}");
        foreach (string note in _notes) TestContext.Out.WriteLine($"[scale:{Layer}] {_scenario}: {note}");
    }

    /// <summary>The checked-in baseline for a scenario, as <c>docs/baselines/&lt;name&gt;.csv</c>.</summary>
    public static string BaselinePath(string name) =>
        Path.Combine(RepositoryRoot(), "docs", "baselines", name + ".csv");

    /// <summary>
    /// The checkout this assembly was built from. Found by the package rather than by <c>.git</c>: in a git
    /// worktree <c>.git</c> is a <i>file</i>, so a directory probe walks past the root and writes the artifacts
    /// somewhere nobody looks.
    /// </summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Packages", "com.1by3.nebula", "package.json")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
