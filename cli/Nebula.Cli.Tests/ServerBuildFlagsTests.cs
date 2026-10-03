using Nebula.Cli.Commands;
using Nebula.Cli.Core;
using NUnit.Framework;

namespace Nebula.Cli.Tests;

/// <summary>The Linux server build flags: what `nebula build` and `nebula deploy` pass on to Unity.</summary>
[TestFixture]
public class ServerBuildFlagsTests
{
    private static UnityBuild.ServerBuildFlags Parse(Command cmd, params string[] argv) => UnityBuild.ServerBuildFlags.FromArgs(ParsedArgs.Parse(argv, cmd));

    [Test]
    public void NoFlagsPassNothingSoTheProjectConfigDecides()
    {
        var flags = Parse(new BuildCommand(), "--linux");
        Assert.That(flags.Development, Is.Null);
        Assert.That(flags.Backend, Is.Null);
        Assert.That(flags.UnityArgs(), Is.Empty);
    }

    [Test]
    public void ReleaseIl2cppBecomesUnitySwitches()
    {
        var flags = Parse(new BuildCommand(), "--linux", "--release-build", "--il2cpp");
        Assert.That(flags.UnityArgs(), Is.EqualTo(new[] { "-nebula-release", "-nebula-il2cpp" }));
    }

    [Test]
    public void DevelopmentMonoBecomesUnitySwitchesOnDeployToo()
    {
        var flags = Parse(new DeployCommand(), "--development-build", "--mono");
        Assert.That(flags.UnityArgs(), Is.EqualTo(new[] { "-nebula-development", "-nebula-mono" }));
    }

    [Test]
    public void ContradictoryFlagsAreRefused()
    {
        Assert.Throws<CliError>(() => Parse(new BuildCommand(), "--release-build", "--development-build"));
        Assert.Throws<CliError>(() => Parse(new BuildCommand(), "--il2cpp", "--mono"));
    }
}
