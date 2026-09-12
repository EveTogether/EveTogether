using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-229: Mutanite is valued at a fixed NPC buy price of 5,000 ISK/unit, never the market's — the one documented
/// exception, checked by SDE group (4568) rather than by name, so it also covers a Mutanite type this build has
/// never seen named.
/// </summary>
public sealed class MiningValuationTests
{
    private const int MutaniteTypeId = 77118; // Amperum Mutanite
    private const int OrdinaryOreTypeId = 45; // Veldspar-shaped fixture, any non-Mutanite group

    private static FakeSdeAccessor Sde() => new FakeSdeAccessor()
        .Add(MutaniteTypeId, "Amperum Mutanite", groupId: MiningValuation.MutaniteGroupId, categoryId: 25)
        .Add(OrdinaryOreTypeId, "Veldspar", groupId: 18, categoryId: 25);

    [Fact]
    public void Mutanite_IsValued_AtTheFixedNpcPrice_RegardlessOfMarketPrice()
    {
        var prices = new Dictionary<int, double> { [MutaniteTypeId] = 1.23 }; // a market price, if any, must be ignored
        decimal? price = MiningValuation.UnitPrice(Sde(), MutaniteTypeId, prices);
        Assert.Equal(MiningValuation.MutaniteNpcBuyPricePerUnit, price);
    }

    [Fact]
    public void Mutanite_IsValued_AtTheFixedNpcPrice_EvenWithNoMarketPriceAtAll()
    {
        decimal? price = MiningValuation.UnitPrice(Sde(), MutaniteTypeId, new Dictionary<int, double>());
        Assert.Equal(5_000m, price);
    }

    [Fact]
    public void OrdinaryOre_IsValued_ThroughTheMarketPrice()
    {
        var prices = new Dictionary<int, double> { [OrdinaryOreTypeId] = 4.5 };
        decimal? price = MiningValuation.UnitPrice(Sde(), OrdinaryOreTypeId, prices);
        Assert.Equal(4.5m, price);
    }

    [Fact]
    public void OrdinaryOre_WithNoMarketPrice_IsUnpriced()
    {
        decimal? price = MiningValuation.UnitPrice(Sde(), OrdinaryOreTypeId, new Dictionary<int, double>());
        Assert.Null(price);
    }

    /// <summary>Residue is depleted, never collected — no ISK value, even for Mutanite. Only <see cref="RunMiningEntry.Units"/>
    /// counts; critical units are already inside it and must not be added a second time.</summary>
    [Fact]
    public void MiningValue_CountsUnitsOnly_NeverResidue_NeverCriticalTwice()
    {
        RunMiningEntry entry = new()
        {
            Id = Guid.NewGuid(),
            RunId = Guid.NewGuid(),
            OreType = "Amperum Mutanite",
            Units = 5_000,
            CriticalUnits = 200,
            ResidueUnits = 1_000
        };

        decimal? value = RunIskFactsReader.MiningValue([entry], Sde(), new Dictionary<int, double>());
        Assert.Equal(25_000_000m, value); // 5,000 units x 5,000 ISK — not 6,000 units, not 25M + residue
    }

    [Fact]
    public void MiningValue_IsNull_WhenNoEntryCanBePriced()
    {
        RunMiningEntry entry = new() { Id = Guid.NewGuid(), RunId = Guid.NewGuid(), OreType = "Unknown Ore" };
        Assert.Null(RunIskFactsReader.MiningValue([entry], Sde(), new Dictionary<int, double>()));
    }

    [Fact]
    public void MiningIskContributor_IsUnknown_WhenThereIsMiningButNothingCanBePricedYet()
    {
        RunIskFacts facts = _Facts(miningIskValue: null, hasMining: true);
        IskContribution? contribution = new MiningIskContributor().Contribute([facts], DateTime.UtcNow);
        Assert.Equal(0m, contribution!.Amount);
        Assert.Equal(IskCertainty.Unknown, contribution.Certainty);
    }

    [Fact]
    public void MiningIskContributor_IsNull_WhenNothingWasEverMined()
    {
        RunIskFacts facts = _Facts(miningIskValue: null, hasMining: false);
        Assert.Null(new MiningIskContributor().Contribute([facts], DateTime.UtcNow));
    }

    [Fact]
    public void MiningIskContributor_SumsPricedMining_AcrossRuns()
    {
        RunIskFacts first = _Facts(miningIskValue: 25_000_000m, hasMining: true);
        RunIskFacts second = _Facts(miningIskValue: 12_500_000m, hasMining: true);
        IskContribution? contribution = new MiningIskContributor().Contribute([first, second], DateTime.UtcNow);
        Assert.Equal(37_500_000m, contribution!.Amount);
        Assert.Equal(IskCertainty.Measured, contribution.Certainty);
    }

    private static RunIskFacts _Facts(decimal? miningIskValue, bool hasMining) => new()
    {
        BountyIsk = 0m,
        LootIskNet = null,
        HasLoot = false,
        ConsumableIskCost = null,
        HasConsumables = false,
        MiningIskValue = miningIskValue,
        HasMining = hasMining,
        Parameters = [],
        StoppedAtUtc = null,
        HomefrontExpectedPayoutIsk = null
    };
}
