using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-519: what a loot table may say. Every figure is worked out by hand: a pilot with up to 10 runs weighs one per run,
/// so 20 pilots with one run each are 20 effective runs, the point from which a drop rate is a percentage.
/// </summary>
public sealed class LootTableStatisticsTests
{
    private const int Tag = 100;
    private const int Rarity = 200;
    private const int Charge = 300;
    private static readonly DateTime NowUtc = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ActivityStatsKey Sanctum = ActivityStatsKey.Site(SiteTypeSource.Site, 2311);

    private static LootTableDto Compute(IReadOnlyList<EarningsSample> samples, Func<int, LootRarityClass>? classify = null,
        Func<int, decimal?>? price = null, LootTableWindow window = LootTableWindow.Days90, DateTime? update = null) =>
        LootTableStatistics.Compute(Sanctum, samples, classify ?? (_ => LootRarityClass.Common), price ?? (_ => 100m),
            LootTablePolicy.Default, window, NowUtc, update);

    private static EarningsSample Run(string pilot, int daysAgo = 1, LootBasis basis = LootBasis.CargoDifference, int fleetSize = 1,
        int unrecognised = 0, IReadOnlyList<(int Type, long Quantity)>? lines = null) => new()
    {
        PilotBucket = pilot,
        CompletedAtUtc = NowUtc.AddDays(-daysAgo),
        FixedIsk = 0m,
        LootBasis = basis,
        FleetSize = fleetSize,
        OpenUnrecognisedLootCount = unrecognised,
        LootGained = [.. (lines ?? []).Select(line => new EarningsLootLine(line.Type, line.Quantity))]
    };

    private static EarningsSample[] Runs(int count, Func<int, (int Type, long Quantity)[]> lines, int firstDaysAgo = 1,
        LootBasis basis = LootBasis.CargoDifference) =>
        [.. Enumerable.Range(0, count).Select(index => Run($"pilot-{index}", firstDaysAgo, basis, lines: lines(index)))];

    private static LootTableItemDto Item(LootTableDto table, int typeId) => table.Items.Single(item => item.TypeId == typeId);

    /// <summary>The denominator is runs with counted loot. 20 counted runs, 10 with the item, and 10 more that were never
    /// pasted: the rate is 10 of 20, not 10 of 30. Counter-proof: count every run in the denominator and the rate is 0.33.</summary>
    [Fact]
    public void DropRate_CountsOnlyRunsWhoseLootWasCounted()
    {
        EarningsSample[] counted = Runs(20, index => index < 10 ? [(Tag, 4)] : [(Charge, 1)]);
        EarningsSample[] pasteOnly = Runs(6, _ => [(Tag, 1)], basis: LootBasis.Captures);
        EarningsSample[] neverPasted = Runs(4, _ => [], basis: LootBasis.None);

        LootTableDto table = Compute([.. counted, .. pasteOnly, .. neverPasted]);

        Assert.Equal(20, table.RunCount);
        Assert.Equal(10, table.UncountedRunCount);
        Assert.Equal(0.5, Item(table, Tag).DropRate, 9);
        Assert.Equal(10, Item(table, Tag).RunCount);
        Assert.Equal(LootTableConfidence.Rated, table.Confidence);
    }

    [Fact]
    public void ARunWithOpenUnrecognisedLines_IsNotCounted()
    {
        EarningsSample[] runs = [.. Runs(20, _ => [(Tag, 1)]), Run("pilot-x", unrecognised: 2, lines: [(Charge, 5)])];

        LootTableDto table = Compute(runs);

        Assert.Equal(20, table.RunCount);
        Assert.Equal(1, table.UncountedRunCount);
        Assert.DoesNotContain(table.Items, item => item.TypeId == Charge);
    }

    /// <summary>A capture holds one pilot's loot, not the activity's, so fleet runs stay out and are counted. Counter-proof:
    /// let them in and the fleet's 5 runs without the item pull the rate under 0.5.</summary>
    [Fact]
    public void FleetRuns_AreLeftOutAndReported()
    {
        EarningsSample[] solo = Runs(20, index => index < 10 ? [(Tag, 2)] : []);
        EarningsSample[] fleet = [.. Enumerable.Range(0, 5).Select(index => Run($"fleet-{index}", fleetSize: 3))];

        LootTableDto table = Compute([.. solo, .. fleet]);

        Assert.Equal(20, table.RunCount);
        Assert.Equal(5, table.FleetRunCount);
        Assert.Equal(0.5, Item(table, Tag).DropRate, 9);
    }

    /// <summary>1 pilot with 200 runs next to 5 pilots with 3: the farmer weighs 10, the others 15, so 25 effective runs.
    /// An item only the farmer finds dropped in 10 of 25 effective runs, not in 200 of 215.</summary>
    [Fact]
    public void Pilots_AreWeighed_AndTheEffectiveCountDecidesTheThreshold()
    {
        EarningsSample[] farmer = [.. Enumerable.Range(0, 200).Select(_ => Run("farmer", lines: [(Tag, 1)]))];
        EarningsSample[] others = [.. Enumerable.Range(0, 15).Select(index => Run($"pilot-{index % 5}", lines: [(Charge, 1)]))];

        LootTableDto table = Compute([.. farmer, .. others]);

        Assert.Equal(215, table.RunCount);
        Assert.Equal(25d, table.EffectiveRunCount, 9);
        Assert.Equal(6, table.PilotCount);
        Assert.Equal(LootTableConfidence.Rated, table.Confidence);
        Assert.Equal(0.4, Item(table, Tag).DropRate, 9);
        Assert.Equal(0.6, Item(table, Charge).DropRate, 9);
        Assert.Equal(200, Item(table, Tag).RunCount);
    }

    [Theory]
    [InlineData(4, LootTableConfidence.None)]
    [InlineData(5, LootTableConfidence.Seen)]
    [InlineData(19, LootTableConfidence.Seen)]
    [InlineData(20, LootTableConfidence.Rated)]
    public void TheMinimum_DecidesWhetherThereIsATable(int runs, LootTableConfidence expected)
    {
        LootTableDto table = Compute(Runs(runs, _ => [(Tag, 1)]));

        Assert.Equal(expected, table.Confidence);
    }

    /// <summary>Below the minimum the names of what was seen are given and no number: no rate, no quantity, no value.</summary>
    [Fact]
    public void BelowTheMinimum_OnlyTheNamesComeBack()
    {
        LootTableDto table = Compute(Runs(3, _ => [(Tag, 7), (Charge, 2)]));

        Assert.Equal(LootTableConfidence.None, table.Confidence);
        Assert.Equal([Tag, Charge], table.Items.Select(item => item.TypeId));
        Assert.All(table.Items, item =>
        {
            Assert.Equal(LootTableConfidence.None, item.Confidence);
            Assert.Equal(0d, item.DropRate);
            Assert.Equal(0d, item.AverageQuantity);
            Assert.Null(item.UnitPrice);
        });
        Assert.Null(table.ExpectedLoot);
    }

    [Fact]
    public void AnItemSeenInUnderThreeRuns_StaysSeenInXOfN_EvenInARatedTable()
    {
        LootTableDto table = Compute(Runs(24, index => index < 2 ? [(Tag, 1), (Charge, 1)] : [(Charge, 1)]));

        Assert.Equal(LootTableConfidence.Rated, table.Confidence);
        Assert.Equal(LootTableConfidence.Seen, Item(table, Tag).Confidence);
        Assert.Equal(2, Item(table, Tag).RunCount);
        Assert.Equal(LootTableConfidence.Rated, Item(table, Charge).Confidence);
    }

    /// <summary>Rare is the catalogue class OR fewer than 5 % of the runs. 1 of 25 is 4 %, 2 of 40 is 5 % and is not rare;
    /// a faction item that dropped every run is rare all the same.</summary>
    [Fact]
    public void Rare_IsTheCatalogueClassOrUnderFivePercentOfRuns()
    {
        LootTableDto fourPercent = Compute(Runs(25, index => index == 0 ? [(Tag, 1)] : [(Charge, 1)]));
        LootTableDto fivePercent = Compute(Runs(40, index => index < 2 ? [(Tag, 1)] : [(Charge, 1)]));
        LootTableDto faction = Compute(Runs(25, _ => [(Rarity, 1), (Charge, 1)]),
            classify: typeId => typeId == Rarity ? LootRarityClass.Faction : LootRarityClass.Common);

        Assert.Equal(LootRarityClass.Seldom, Item(fourPercent, Tag).RarityClass);
        Assert.True(Item(fourPercent, Tag).IsRare);
        Assert.Equal(LootRarityClass.Common, Item(fivePercent, Tag).RarityClass);
        Assert.False(Item(fivePercent, Tag).IsRare);
        Assert.Equal(LootRarityClass.Faction, Item(faction, Rarity).RarityClass);
        Assert.True(Item(faction, Rarity).IsRare);
        Assert.False(Item(faction, Charge).IsRare);
        Assert.Equal(1, faction.RareKindCount);
        Assert.Equal(25, faction.RareRunCount);
    }

    /// <summary>Where a rate is not trusted yet (under 20 runs) frequency does not make anything rare; the catalogue class still does.</summary>
    [Fact]
    public void Frequency_MakesNothingRare_BelowTwentyRuns()
    {
        LootTableDto table = Compute(Runs(10, index => index == 0 ? [(Tag, 1)] : [(Charge, 1)]));

        Assert.Equal(LootTableConfidence.Seen, table.Confidence);
        Assert.False(Item(table, Tag).IsRare);
    }

    [Fact]
    public void WithoutTheCatalogue_RarityRestsOnFrequencyAlone()
    {
        LootTableDto table = Compute(Runs(25, index => index == 0 ? [(Tag, 1)] : [(Charge, 1)]), classify: _ => LootRarityClass.Common);

        Assert.Equal(LootRarityClass.Seldom, Item(table, Tag).RarityClass);
        Assert.Equal(LootRarityClass.Common, Item(table, Charge).RarityClass);
    }

    [Theory]
    [InlineData(9, null, LootRarityClass.Blueprint)]
    [InlineData(7, 3, LootRarityClass.Storyline)]
    [InlineData(7, 4, LootRarityClass.Faction)]
    [InlineData(7, 5, LootRarityClass.Officer)]
    [InlineData(7, 6, LootRarityClass.Deadspace)]
    [InlineData(7, 1, LootRarityClass.Common)]
    [InlineData(7, null, LootRarityClass.Common)]
    [InlineData(9, 4, LootRarityClass.Blueprint)]
    public void Classify_TellsTheCatalogueClasses(int categoryId, int? metaGroupId, LootRarityClass expected) =>
        Assert.Equal(expected, LootRarity.Classify(categoryId, metaGroupId));

    /// <summary>The classes come out of the SDE: the category for a blueprint, the meta group for the rest.</summary>
    [Fact]
    public void Of_ReadsTheClassOfAnItemOutOfTheSde()
    {
        FakeSdeAccessor sde = new FakeSdeAccessor()
            .Add(Tag, "Plain", groupId: 1, categoryId: 7)
            .Add(Rarity, "Faction Thing", groupId: 1, categoryId: 7, metaGroupId: 4)
            .Add(Charge, "Some Blueprint", groupId: 2, categoryId: 9);

        Func<int, LootRarityClass> classify = LootRarity.Of(sde);

        Assert.Equal(LootRarityClass.Common, classify(Tag));
        Assert.Equal(LootRarityClass.Faction, classify(Rarity));
        Assert.Equal(LootRarityClass.Blueprint, classify(Charge));
        Assert.Equal(LootRarityClass.Common, classify(999_999));
    }

    [Fact]
    public void Of_WithoutTheSdeStore_EverythingIsCommon()
    {
        FakeSdeAccessor sde = new FakeSdeAccessor().Add(Rarity, "Faction Thing", groupId: 1, categoryId: 7, metaGroupId: 4).Offline();

        Assert.Equal(LootRarityClass.Common, LootRarity.Of(sde)(Rarity));
    }

    /// <summary>A blueprint is valued by its build appraisal and never by a market price, and is a rare drop labelled Blueprint.</summary>
    [Fact]
    public async Task ABlueprint_IsValuedByTheAppraisal_AndGroupedAsRare()
    {
        const int blueprint = 11_111;
        var appraisal = new FakeAppraisal(new Dictionary<int, decimal?> { [blueprint] = 750_000m });
        var market = new FakeMarket(new Dictionary<int, double> { [blueprint] = 9_999_999d, [Tag] = 100d });

        Func<int, decimal?> price = await TodaysLootPrices.ForAsync(market, appraisal, new FakeSdeAccessor(), [blueprint, Tag],
            TestContext.Current.CancellationToken);
        LootTableDto table = Compute(Runs(20, _ => [(blueprint, 1), (Tag, 3)]), price: price,
            classify: typeId => typeId == blueprint ? LootRarityClass.Blueprint : LootRarityClass.Common);

        LootTableItemDto item = Item(table, blueprint);
        Assert.Equal(750_000m, item.UnitPrice);
        Assert.Equal(LootRarityClass.Blueprint, item.RarityClass);
        Assert.True(item.IsRare);
        Assert.Equal(300m, Item(table, Tag).ValuePerDrop);
    }

    /// <summary>A blueprint the appraisal cannot price in full is unpriced, not valued at a market price for something it is not.</summary>
    [Fact]
    public async Task ABlueprintTheAppraisalCannotPrice_IsReportedAsUnpriced()
    {
        const int blueprint = 11_111;
        var appraisal = new FakeAppraisal(new Dictionary<int, decimal?> { [blueprint] = null });
        var market = new FakeMarket(new Dictionary<int, double> { [blueprint] = 9_999_999d });

        Func<int, decimal?> price = await TodaysLootPrices.ForAsync(market, appraisal, new FakeSdeAccessor(), [blueprint],
            TestContext.Current.CancellationToken);
        LootTableDto table = Compute(Runs(20, _ => [(blueprint, 1)]), price: price);

        Assert.Null(Item(table, blueprint).UnitPrice);
        Assert.Equal(1, table.UnpricedItemCount);
    }

    [Fact]
    public void AnItemWithoutAPrice_CountsAsZero_AndIsReported()
    {
        LootTableDto table = Compute(Runs(20, _ => [(Tag, 2), (Charge, 1)]), price: typeId => typeId == Tag ? 50m : null);

        Assert.Equal(1, table.UnpricedItemCount);
        Assert.Null(Item(table, Charge).UnitPrice);
        Assert.Equal(0m, Item(table, Charge).Contribution);
        Assert.Equal(100m, table.ExpectedLoot);
    }

    /// <summary>Two lines of one type in a run are one run with that item, and their quantities add up.</summary>
    [Fact]
    public void TheSameTypeOnTwoLines_IsOneRun_WithTheQuantitiesAdded()
    {
        EarningsSample[] runs = [.. Runs(19, _ => [(Charge, 1)]), Run("pilot-x", lines: [(Tag, 3), (Tag, 4)])];

        LootTableItemDto item = Item(Compute(runs), Tag);

        Assert.Equal(1, item.RunCount);
        Assert.Equal(7, item.MinimumQuantity);
        Assert.Equal(7d, item.AverageQuantity, 9);
    }

    /// <summary>The figures of one item, by hand: in 10 of 20 runs, 4 each, at 25 ISK. Rate 0.5, average 4, worth 100 a drop,
    /// 50 into the expected loot of a run, which with a second item of 50 is a share of one half.</summary>
    [Fact]
    public void AnItem_GivesRateQuantityValueAndShare()
    {
        LootTableDto table = Compute(Runs(20, index => index < 10 ? [(Tag, 4), (Charge, 2)] : [(Charge, 2)]),
            price: typeId => typeId == Tag ? 25m : 25m);

        LootTableItemDto item = Item(table, Tag);
        Assert.Equal(0.5, item.DropRate, 9);
        Assert.Equal(4d, item.AverageQuantity, 9);
        Assert.Equal(100m, item.ValuePerDrop);
        Assert.Equal(50m, item.Contribution);
        Assert.Equal(100m, table.ExpectedLoot);
        Assert.Equal(0.5, item.ValueShare, 9);
        Assert.Equal(Charge, table.Items[0].TypeId);
        Assert.InRange(item.DropRateLow, 0.3, 0.5);
        Assert.InRange(item.DropRateHigh, 0.5, 0.7);
    }

    /// <summary>The sum of the table is the mean loot of a run and sits above the median where a rare drop pulls it up.</summary>
    [Fact]
    public void TheExpectedLoot_CanSitAboveTheTypicalLoot()
    {
        LootTableDto table = Compute(Runs(20, index => index == 0 ? [(Tag, 1), (Charge, 1)] : [(Charge, 1)]),
            price: typeId => typeId == Tag ? 10_000m : 100m);

        Assert.Equal(100m + 10_000m / 20, table.ExpectedLoot);
        Assert.Equal(100m, table.TypicalLoot);
        Assert.True(table.ExpectedLoot > table.TypicalLoot);
        Assert.Equal(table.Items.Sum(item => item.Contribution), table.ExpectedLoot);
    }

    [Fact]
    public void TheWindow_LeavesOutOlderRuns()
    {
        EarningsSample[] recent = Runs(20, _ => [(Tag, 1)], firstDaysAgo: 5);
        EarningsSample[] old = Runs(20, _ => [(Charge, 1)], firstDaysAgo: 60);

        LootTableDto thirty = Compute([.. recent, .. old], window: LootTableWindow.Days30);
        LootTableDto ninety = Compute([.. recent, .. old], window: LootTableWindow.Days90);

        Assert.Equal(20, thirty.RunCount);
        Assert.DoesNotContain(thirty.Items, item => item.TypeId == Charge);
        Assert.Equal(40, ninety.RunCount);
    }

    [Fact]
    public void SinceUpdate_ReadsFromTheUpdate_AndNeverBeyondNinetyDays()
    {
        EarningsSample[] runs = [.. Runs(20, _ => [(Tag, 1)], firstDaysAgo: 3), .. Runs(20, _ => [(Charge, 1)], firstDaysAgo: 20)];

        LootTableDto since = Compute(runs, window: LootTableWindow.SinceUpdate, update: NowUtc.AddDays(-10));
        LootTableDto longAgo = Compute(runs, window: LootTableWindow.SinceUpdate, update: NowUtc.AddDays(-400));

        Assert.Equal(20, since.RunCount);
        Assert.Equal(40, longAgo.RunCount);
    }

    /// <summary>"Not seen since update" needs at least 8 runs on each side. With 7 after the update the flag is not given;
    /// with 8 it is. Counter-proof: lower the minimum to 7 and the first assertion goes red.</summary>
    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    public void TheUpdateFlags_NeedEightRunsOnBothSides(int runsAfter, bool flagged)
    {
        DateTime update = NowUtc.AddDays(-10);
        EarningsSample[] before = Runs(8, _ => [(Tag, 1), (Charge, 1)], firstDaysAgo: 30);
        EarningsSample[] after = Runs(runsAfter, _ => [(Charge, 1)], firstDaysAgo: 3);

        LootTableDto table = Compute([.. before, .. after], update: update);

        LootItemChange expected = flagged ? LootItemChange.NotSeenSinceUpdate : LootItemChange.None;
        Assert.Equal(expected, Item(table, Tag).Change);
        Assert.Equal(flagged, table.HasUpdateFlags);
    }

    [Fact]
    public void AnItemOnlySeenAfterTheUpdate_IsFlaggedNew()
    {
        DateTime update = NowUtc.AddDays(-10);
        EarningsSample[] before = Runs(10, _ => [(Charge, 1)], firstDaysAgo: 30);
        EarningsSample[] after = Runs(10, index => index < 3 ? [(Tag, 1), (Charge, 1)] : [(Charge, 1)], firstDaysAgo: 3);

        LootTableDto table = Compute([.. before, .. after], update: update);

        Assert.Equal(LootItemChange.NewSinceUpdate, Item(table, Tag).Change);
        Assert.Equal(LootItemChange.None, Item(table, Charge).Change);
    }

    [Fact]
    public void NoSamples_GiveAnEmptyTable_WithoutThrowing()
    {
        LootTableDto table = Compute([]);

        Assert.Equal(LootTableConfidence.None, table.Confidence);
        Assert.Empty(table.Items);
        Assert.Equal(0, table.RunCount);
    }

    [Fact]
    public void WilsonInterval_StaysInsideZeroAndOne_AndDoesNotCollapseAtTheEdges()
    {
        (double low, double high) = LootTableStatistics.WilsonInterval(20, 20, 1.645);

        Assert.InRange(low, 0.85, 0.99);
        Assert.Equal(1d, high, 9);
        Assert.Equal((0d, 1d), LootTableStatistics.WilsonInterval(0, 0, 1.645));
    }

    private sealed class FakeAppraisal(IReadOnlyDictionary<int, decimal?> values) : IBlueprintAppraisalService
    {
        public Task<BlueprintAppraisal?> AppraiseAsync(int blueprintTypeId, int runs, int materialEfficiency,
            CancellationToken cancellationToken = default) => Task.FromResult<BlueprintAppraisal?>(null);

        public Task<IReadOnlyDictionary<int, decimal?>> GetLootValuesAsync(IReadOnlyCollection<int> typeIds,
            CancellationToken cancellationToken = default) => Task.FromResult(values);
    }

    private sealed class FakeMarket(IReadOnlyDictionary<int, double> averages) : IMarketPriceRepository
    {
        public Task ReplaceAllAsync(IReadOnlyCollection<LocalMarketPrice> prices, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyDictionary<int, double>> GetAveragePricesAsync(IReadOnlyCollection<int> typeIds,
            CancellationToken cancellationToken = default) => Task.FromResult(averages);

        public Task<IReadOnlyDictionary<int, double>> GetAdjustedPricesAsync(IReadOnlyCollection<int> typeIds,
            CancellationToken cancellationToken = default) => Task.FromResult(averages);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) => Task.FromResult(averages.Count);

        public Task<DateTimeOffset?> GetSnapshotTimeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTimeOffset?>(null);
    }
}
