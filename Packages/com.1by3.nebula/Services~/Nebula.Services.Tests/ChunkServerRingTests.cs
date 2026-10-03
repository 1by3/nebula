using Nebula;
using NUnit.Framework;

namespace Nebula.ServiceTests;

/// <summary>
/// The worker's chunk ring is <c>NearCells</c> plus <c>ChunkedWorldServerRingMargin</c> (NEB-380): the default
/// keeps the long-standing <c>NearCells + 1</c>, 0 leases only what interest can reach, and a margin that leaves
/// too little time to build a newly requested chunk is reported by <c>Validate</c>.
/// </summary>
[TestFixture]
public class ChunkServerRingTests
{
    // 120 m radius, 16 m exit margin, 12 m/s, 1 s linger, 4 Hz: reach 151 m, so 256 m chunks need 1 cell.
    private const float Cell = 256f;

    private static NebulaConfig Config(int margin) => new()
    {
        ChunkedWorld = true,
        InterestRadius = 120f,
        InterestExitMargin = 16f,
        InterestMaxFocusSpeed = 12f,
        InterestLingerSeconds = 1f,
        InterestEvalHz = 4f,
        ChunkedWorldServerRingMargin = margin,
    };

    private static int Ring(System.Collections.Generic.List<ConfigIssue> issues) =>
        issues.FindAll(i => i.Field == nameof(NebulaConfig.ChunkedWorldServerRingMargin)).Count;

    [Test]
    public void TheDefaultMarginIsOneCellBeyondNearCells()
    {
        var config = new NebulaConfig();
        Assert.That(config.ChunkedWorldServerRingMargin, Is.EqualTo(1));
        var settings = Config(1).ToInterestSettings(Cell);
        Assert.That(settings.NearCells(Cell), Is.EqualTo(1));
        Assert.That(settings.ChunkRing(Cell, config.ChunkedWorldServerRingMargin), Is.EqualTo(2));
    }

    [TestCase(0, 1)]
    [TestCase(1, 2)]
    [TestCase(2, 3)]
    [TestCase(-3, 1)] // never below NearCells
    public void TheRingIsNearCellsPlusTheMargin(int margin, int expected) =>
        Assert.That(Config(margin).ToInterestSettings(Cell).ChunkRing(Cell, margin), Is.EqualTo(expected));

    [Test]
    public void LeadTimeGrowsByACellOfTravelPerMarginCell()
    {
        var settings = Config(0).ToInterestSettings(Cell);
        // 256 - 136 - 12 x 1.25 = 105 m at 12 m/s.
        Assert.That(settings.ChunkLoadLeadSeconds(Cell, 0), Is.EqualTo(105f / 12f).Within(0.01f));
        Assert.That(settings.ChunkLoadLeadSeconds(Cell, 1), Is.EqualTo(361f / 12f).Within(0.01f));
    }

    [Test]
    public void MarginZeroIsSafeForTheHoloverseNumbersAndValidateStaysQuiet()
    {
        var issues = new System.Collections.Generic.List<ConfigIssue>();
        Config(0).Validate(issues, Cell);
        Assert.That(Ring(issues), Is.Zero);
    }

    [Test]
    public void ValidateWarnsWhenMarginZeroLeavesTooLittleTimeToBuildAChunk()
    {
        // A 151 m cell makes NearCells exactly 1 with no rounding slack, so margin 0 has no lead time at all.
        var issues = new System.Collections.Generic.List<ConfigIssue>();
        Config(0).Validate(issues, 151f);
        Assert.That(Ring(issues), Is.EqualTo(1));
        issues.Clear();
        Config(1).Validate(issues, 151f);
        Assert.That(Ring(issues), Is.Zero);
    }

    [Test]
    public void ValidateWarnsAboutANegativeMargin()
    {
        var issues = new System.Collections.Generic.List<ConfigIssue>();
        Config(-1).Validate(issues, Cell);
        Assert.That(Ring(issues), Is.EqualTo(1));
    }
}
