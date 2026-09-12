using EveUtils.Shared.Modules.Gamelog.Aggregation;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-229: the residue line names no ore, so it is attributed to whichever ore that character was last
/// seen mining. Per character, since each client watches only its own gamelog.</summary>
public sealed class MiningResidueCorrelatorTests
{
    [Fact]
    public void OreFor_ReturnsTheLastObservedOre_ForThatCharacter()
    {
        var correlator = new MiningResidueCorrelator();
        correlator.Observe("Jithran", "Amperum Mutanite");
        correlator.Observe("Jithran", "Conflagrati Mutanite");

        Assert.Equal("Conflagrati Mutanite", correlator.OreFor("Jithran"));
    }

    [Fact]
    public void OreFor_ReturnsNull_BeforeAnyMiningLineWasSeenForThatCharacter()
    {
        var correlator = new MiningResidueCorrelator();
        Assert.Null(correlator.OreFor("Jithran"));
    }

    [Fact]
    public void OreFor_NeverCrossesCharacters()
    {
        var correlator = new MiningResidueCorrelator();
        correlator.Observe("Jithran", "Amperum Mutanite");

        Assert.Null(correlator.OreFor("Abnoba Auscent"));
    }
}
