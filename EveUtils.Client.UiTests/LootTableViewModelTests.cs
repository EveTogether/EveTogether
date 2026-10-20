using EveUtils.Client.Opsec;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-519: the loot table's view model in its three states (enough runs, few, too few), the sort and the rare group, the
/// window and source switches, the site picker, and the entry points that open it. The figures are worked out in
/// <see cref="LootTableStatisticsTests"/>; here only what a reader is told.
/// </summary>
public sealed class LootTableViewModelTests
{
    internal const int Tag = 100;
    internal const int Rarity = 200;
    internal const int Charge = 300;
    internal static readonly ActivityStatsKey Sanctum = ActivityStatsKey.Site(SiteTypeSource.Site, 2311);
    internal static readonly ActivityStatsKey Cache = ActivityStatsKey.Site(SiteTypeSource.Site, 2316);
    private static readonly DateTime NowUtc = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static LootTableDto Table(ActivityStatsKey key, int runs, LootTableWindow window = LootTableWindow.Days90,
        int fleetRuns = 0, int uncountedRuns = 0, Func<int, decimal?>? price = null)
    {
        List<EarningsSample> samples = [.. Enumerable.Range(0, runs).Select(index => new EarningsSample
        {
            PilotBucket = $"pilot-{index}",
            CompletedAtUtc = NowUtc.AddDays(-1 - index % 20),
            FixedIsk = 1_000_000m,
            LootBasis = LootBasis.CargoDifference,
            LootGained =
            [
                new EarningsLootLine(Charge, 5),
                .. index % 2 == 0 ? [new EarningsLootLine(Tag, 3)] : Array.Empty<EarningsLootLine>(),
                .. index == 0 ? [new EarningsLootLine(Rarity, 1)] : Array.Empty<EarningsLootLine>()
            ]
        })];
        samples.AddRange(Enumerable.Range(0, fleetRuns).Select(index => new EarningsSample
        {
            PilotBucket = $"fleet-{index}", CompletedAtUtc = NowUtc.AddDays(-2), FixedIsk = 0m, FleetSize = 3, LootBasis = LootBasis.CargoDifference
        }));
        samples.AddRange(Enumerable.Range(0, uncountedRuns).Select(index => new EarningsSample
        {
            PilotBucket = $"loose-{index}", CompletedAtUtc = NowUtc.AddDays(-2), FixedIsk = 0m, LootBasis = LootBasis.Captures
        }));
        return LootTableStatistics.Compute(key, samples,
            typeId => typeId == Rarity ? LootRarityClass.Faction : LootRarityClass.Common, price ?? (typeId => typeId switch
            {
                Charge => 1_000m,
                Tag => 10_000m,
                _ => 5_000_000m
            }), LootTablePolicy.Default, window, NowUtc, null);
    }

    internal sealed class FakeSource : ILootTableSource
    {
        public Func<LootTableRequest, LootTableReading>? Script { get; set; }
        public List<LootTableRequest> Requests { get; } = [];
        public int Runs { get; set; } = 30;
        public IReadOnlyList<LootSiteChoice> Flown { get; set; } =
        [
            new(Sanctum, "Angel Sanctum", 30, NowUtc.AddDays(-1)),
            new(Cache, "Crimson Cache", 3, NowUtc.AddDays(-9))
        ];

        public Task<LootTableReading> ReadAsync(LootTableRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Script?.Invoke(request) ?? new LootTableReading(Table(request.Key, Runs, request.Window), request.Basis,
                request.Basis is EarningsBasis.AllPilots ? "Krah Server" : null, OwnRunsReason.None, NowUtc));
        }

        public Task<IReadOnlyList<LootSiteChoice>> ListFlownAsync(CancellationToken cancellationToken = default) => Task.FromResult(Flown);
    }

    private static LootTableViewModel Create(FakeSource source, TimeProvider? time = null) =>
        new(source, new ActivityStatsLabels(null, new RunRowFacts(null)), time: time ?? new FixedTime());

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(NowUtc);
    }

    [Fact]
    public async Task EnoughRuns_ShowAPercentageRateAndTheShareOfValue()
    {
        var source = new FakeSource { Runs = 30 };
        LootTableViewModel table = Create(source);

        await table.ShowAsync(Sanctum, [1]);

        Assert.True(table.ShowsTable);
        Assert.True(table.HasTable);
        Assert.False(table.HasFewRunsText);
        LootTableRowViewModel charge = table.Items.Single(row => row.Item.TypeId == Charge);
        Assert.Equal("100 %", charge.RateText);
        Assert.True(charge.ShowsRatePercentage);
        Assert.True(charge.ShowsShareText);
        Assert.Equal("×5.0", charge.QuantityText);
        Assert.Contains("%", charge.ShareText);
        Assert.StartsWith("based on 30 runs · 30 pilots · last 90 days", table.BasedOnText);
        Assert.True(table.HasSummaryFigures);
        Assert.NotEmpty(table.ExpectedText);
        Assert.NotEmpty(table.TypicalText);
    }

    [Fact]
    public async Task FewRuns_ShowSeenInXOfN_WithABannerAndNoShareNumber()
    {
        LootTableViewModel table = Create(new FakeSource { Runs = 12 });

        await table.ShowAsync(Sanctum, [1]);

        Assert.True(table.HasTable);
        Assert.True(table.HasFewRunsText);
        Assert.Contains("n=12", table.FewRunsText);
        LootTableRowViewModel tag = table.Items.Single(row => row.Item.TypeId == Tag);
        Assert.Equal("seen in 6 of 12", tag.RateText);
        Assert.False(tag.ShowsRatePercentage);
        Assert.False(tag.ShowsShareText);
        Assert.DoesNotContain("%", tag.RateText);
    }

    [Fact]
    public async Task TooFewRuns_ShowNoTable_OnlyTheNamesOfWhatWasSeen()
    {
        LootTableViewModel table = Create(new FakeSource { Runs = 3 });

        await table.ShowAsync(Sanctum, [1]);

        Assert.False(table.HasTable);
        Assert.True(table.HasSeenNames);
        Assert.Equal("Not enough runs yet — 3 of 5 needed", table.TooFewText);
        Assert.Equal([true, true, true, false, false], table.RunDots);
        Assert.Empty(table.Items);
        Assert.Equal(3, table.SeenNames.Count);
        Assert.All(table.SeenNames, row =>
        {
            Assert.False(row.HasNumbers);
            Assert.Equal(string.Empty, row.RateText);
            Assert.Equal(string.Empty, row.ValueText);
        });
        Assert.False(table.HasSummaryFigures);
    }

    /// <summary>The rare drop sits in its own folded group: one line with what the rare drops come to, and the rows only
    /// when it is opened. Only a count of runs, never a percentage, in the rows of a table that is not rated.</summary>
    [Fact]
    public async Task TheRareGroup_IsFoldedUntilOpened_AndKeepsItsStateOnAKeySwap()
    {
        LootTableViewModel table = Create(new FakeSource { Runs = 30 });
        await table.ShowAsync(Sanctum, [1]);

        Assert.True(table.HasRare);
        Assert.Empty(table.RareItems);
        Assert.DoesNotContain(table.Items, row => row.Item.TypeId == Rarity);
        Assert.Equal("RARE DROPS", table.RareTitleText);
        Assert.Contains("1 kinds", table.RareDetailText);
        Assert.Equal("seen in 3 % of runs · 89 % of value", table.RareFigureText);

        table.ToggleRareCommand.Execute(null);
        Assert.Equal("▼", table.RareArrow);
        LootTableRowViewModel rare = Assert.Single(table.RareItems);
        Assert.Equal("Faction", rare.TagText);
        Assert.Equal("seen in 1 of 30", rare.RateText);

        await table.ShowAsync(Cache, [1]);

        Assert.True(table.IsRareOpen);
        Assert.Single(table.RareItems);
    }

    [Fact]
    public async Task Sorting_FollowsTheColumn_ThenFlipsOnASecondClick_AndSurvivesAKeySwap()
    {
        LootTableViewModel table = Create(new FakeSource { Runs = 30 });
        await table.ShowAsync(Sanctum, [1]);

        Assert.Equal(LootTableSort.Share, table.Sort);
        Assert.Equal([Tag, Charge], table.Items.Select(row => row.Item.TypeId));

        table.SortByRateCommand.Execute(null);
        Assert.Equal([Charge, Tag], table.Items.Select(row => row.Item.TypeId));
        Assert.Equal(" ▼", table.RateMark);

        table.SortByRateCommand.Execute(null);
        Assert.Equal([Tag, Charge], table.Items.Select(row => row.Item.TypeId));
        Assert.Equal(" ▲", table.RateMark);

        table.SortByNameCommand.Execute(null);
        Assert.False(table.IsDescending);

        await table.ShowAsync(Cache, [1]);

        Assert.Equal(LootTableSort.Name, table.Sort);
    }

    [Fact]
    public async Task TheTable_SaysWhatWasLeftOut_AndWhatHasNoPrice()
    {
        var source = new FakeSource
        {
            Script = request => new LootTableReading(Table(request.Key, 30, fleetRuns: 4, uncountedRuns: 2,
                price: typeId => typeId == Charge ? null : 100m), EarningsBasis.YourRuns, null, OwnRunsReason.None, NowUtc)
        };
        LootTableViewModel table = Create(source);

        await table.ShowAsync(Sanctum, [1]);

        Assert.True(table.HasLeftOut);
        Assert.Contains("2 runs without a loot count", table.LeftOutText);
        Assert.Contains("4 fleet runs", table.LeftOutText);
        Assert.Equal("1 item has no price and counts as 0.", table.UnpricedText);
        LootTableRowViewModel noPrice = table.Items.Single(row => row.Item.TypeId == Charge);
        Assert.Equal("no price", noPrice.ValueText);
    }

    [Fact]
    public async Task ABlueprint_IsLabelledBlueprint_AndValuedByBuildAppraisal()
    {
        LootTableDto blueprint = LootTableStatistics.Compute(Sanctum,
            [.. Enumerable.Range(0, 20).Select(index => new EarningsSample
            {
                PilotBucket = $"pilot-{index}", CompletedAtUtc = NowUtc.AddDays(-1), FixedIsk = 0m, LootBasis = LootBasis.CargoDifference,
                LootGained = [new EarningsLootLine(Rarity, 1)]
            })], _ => LootRarityClass.Blueprint, _ => 750_000m, LootTablePolicy.Default, LootTableWindow.Days90, NowUtc, null);
        LootTableViewModel table = Create(new FakeSource
        {
            Script = request => new LootTableReading(blueprint, request.Basis, null, OwnRunsReason.None, NowUtc)
        });
        await table.ShowAsync(Sanctum, [1]);
        table.ToggleRareCommand.Execute(null);

        LootTableRowViewModel row = Assert.Single(table.RareItems);

        Assert.Equal("Blueprint", row.TagText);
        Assert.Equal("build appraisal", row.ValueSub);
    }

    [Fact]
    public async Task TheWindowSwitch_ReadsAgainOverThatWindow_AndSinceUpdateIsNotOfferedWithoutAnUpdate()
    {
        var source = new FakeSource();
        LootTableViewModel table = Create(source);
        await table.ShowAsync(Sanctum, [1]);

        await table.Show30DaysCommand.ExecuteAsync(null);
        await table.ShowSinceUpdateCommand.ExecuteAsync(null);

        Assert.Equal([LootTableWindow.Days90, LootTableWindow.Days30, LootTableWindow.SinceUpdate], source.Requests.Select(request => request.Window));
        Assert.False(table.HasUpdate);
    }

    [Fact]
    public async Task YourRuns_AsksForTheOwnRuns_AndAServerThatCannotAnswerIsSaidNotToastedAndNotAnError()
    {
        var source = new FakeSource
        {
            Script = request => new LootTableReading(Table(request.Key, 30), EarningsBasis.YourRuns, null, OwnRunsReason.TooFewPilots, NowUtc)
        };
        LootTableViewModel table = Create(source);

        await table.ShowAsync(Sanctum, [1]);

        Assert.Equal(EarningsBasis.AllPilots, source.Requests[0].Basis);
        Assert.Equal("YOUR RUNS", table.SourceChip);
        Assert.False(table.IsSharedSource);
        Assert.Contains("Too few pilots share this site", table.NoteText);
        Assert.True(table.ShowsTable);

        await table.ShowYourRunsCommand.ExecuteAsync(null);

        Assert.Equal(EarningsBasis.YourRuns, source.Requests[^1].Basis);
        Assert.Equal(string.Empty, table.NoteText);
    }

    [Fact]
    public async Task AllPilots_ShowsTheServerItCameFrom()
    {
        LootTableViewModel table = Create(new FakeSource());

        await table.ShowAsync(Sanctum, [1]);

        Assert.Equal("ALL PILOTS · Krah Server", table.SourceChip);
        Assert.True(table.IsSharedSource);
    }

    /// <summary>OPSEC (ET-417): the site's name is marked as a location; the names of the items are not.</summary>
    [Fact]
    public async Task TheSiteName_IsMarkedForOpsec_ButNotTheItems()
    {
        LootTableViewModel table = Create(new FakeSource());

        await table.ShowAsync(Sanctum, [1]);

        Assert.Equal(OpsecText.Mark("Angel Sanctum"), table.NameText);
        Assert.True(OpsecText.IsMarked(table.NameText));
        Assert.All(table.Items, row => Assert.False(OpsecText.IsMarked(row.NameText)));
        Assert.False(OpsecText.IsMarked(table.KindText));
        Assert.False(OpsecText.IsMarked(table.BasedOnText));
    }

    [Fact]
    public async Task WithoutASite_TheFlownActivitiesAreOffered_SearchedLikeTheRunsOverview_AndAPickOpensTheTable()
    {
        var source = new FakeSource();
        LootTableViewModel table = Create(source);

        await table.ShowAsync(null, [1]);

        Assert.True(table.IsPicking);
        Assert.Equal(["Angel Sanctum", "Crimson Cache"], table.Choices.Select(choice => OpsecText.Strip(choice.NameText)));
        Assert.Empty(source.Requests);

        table.SearchText = "crimson";
        LootSiteChoiceViewModel crimson = Assert.Single(table.Choices);
        Assert.Contains("3 runs", crimson.MetaText);
        table.SearchText = "nothing like it";
        Assert.True(table.HasNoChoices);
        Assert.Equal("No site matches.", table.NoChoicesText);

        table.SearchText = string.Empty;
        table.Choices[0].PickCommand.Execute(null);
        await WaitAsync(() => table.ShowsTable);

        Assert.Equal(Sanctum, source.Requests.Single().Key);
    }

    [Fact]
    public async Task WithoutAnyFlownActivity_TheWindowSaysSo()
    {
        LootTableViewModel table = Create(new FakeSource { Flown = [] });

        await table.ShowAsync(null, []);

        Assert.True(table.HasNoChoices);
        Assert.StartsWith("No activity flown yet", table.NoChoicesText);
    }

    // ── Entry points ─────────────────────────────────────────────────────────────────────────────────────────

    internal sealed class RecordingOpener : ILootTableOpener
    {
        public List<(ActivityStatsKey? Key, IReadOnlyList<int> Characters)> Opened { get; } = [];

        public void Open(ActivityStatsKey? key, IReadOnlyList<int> characterIds) => Opened.Add((key, characterIds));
    }

    internal static async Task WaitAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(10, Token);

        Assert.True(condition(), "the condition was not met in time");
    }
}
