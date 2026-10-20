using Avalonia.Headless.XUnit;
using EveUtils.Client.Messaging;
using EveUtils.Client.Runs;
using EveUtils.Client.Transport;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-520: where a saved run stands among the runs of its activity. The rank is the part that can be wrong in a way no
/// screen shows: a run counted against itself reads better than the truth, and a tie read as a win reads better still.
/// </summary>
public sealed class RunPositionTests(ITestOutputHelper output)
{
    private const long Pilot = 90000001;
    private const int Tritanium = 34;
    private const int SiteTypeId = 2311;
    private static readonly ActivityStatsKey Sanctum = ActivityStatsKey.Site(SiteTypeSource.Site, SiteTypeId);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ── The rank ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The best of six runs beat all five of the others: 100 %, not the 91.7 % it reads at when it counts a tie
    /// against itself. Counter-proof: pass isInDistribution false and this reads (5 + 0.5) / 6.</summary>
    [Fact]
    public void Rank_OfTheBestOwnRun_IsNotCountedAgainstItself()
    {
        EarningsYieldDto[] yields = [.. Enumerable.Range(1, 6).Select(index => Yield(index * 1000m, "run-" + index))];

        RunStanding? rank = RunRank.Of(yields, 6000m, "run-6", isInDistribution: true, minimumOthers: 5);

        Assert.Equal(1.0, rank?.Percentile);
        Assert.Equal(5, rank?.OtherRunCount);
        Assert.Equal((5 + 0.5) / 6, RunRank.Of(yields, 6000m, "run-6", isInDistribution: false, minimumOthers: 5)?.Percentile);
    }

    [Fact]
    public void Rank_OfAMiddleRun_CountsOnlyTheOthersBelowIt()
    {
        EarningsYieldDto[] yields = [.. Enumerable.Range(1, 6).Select(index => Yield(index * 1000m, "run-" + index))];

        RunStanding? rank = RunRank.Of(yields, 3000m, "run-3", isInDistribution: true, minimumOthers: 5);

        Assert.Equal(2d / 5, rank?.Percentile);
    }

    /// <summary>A tie is half a win: a run that earned exactly what every other did is better than half of them, not
    /// than none and not than all.</summary>
    [Fact]
    public void Rank_WithTies_CountsATieAsHalf()
    {
        EarningsYieldDto[] yields = [Yield(1m), Yield(2m), Yield(2m), Yield(2m), Yield(5m)];

        Assert.Equal(0.5, RunRank.Of(yields, 2m, null, isInDistribution: false, minimumOthers: 5)?.Percentile);
        Assert.Equal(0.5, RunRank.Of([Yield(7m), Yield(7m), Yield(7m), Yield(7m), Yield(7m)], 7m, null, false, 5)?.Percentile);
    }

    /// <summary>A server's outcomes carry no reference: the run's own is the entry of the same yield, one of them.</summary>
    [Fact]
    public void Rank_AmongAServersOutcomes_TakesOutOneEntryOfTheSameYield()
    {
        EarningsYieldDto[] yields = [.. Enumerable.Range(1, 6).Select(index => Yield(index * 10m))];

        Assert.Equal(1.0, RunRank.Of(yields, 60m, "run", isInDistribution: true, minimumOthers: 5)?.Percentile);
        // No entry of 25: nothing to take out, six others stand.
        RunStanding? between = RunRank.Of(yields, 25m, "run", isInDistribution: true, minimumOthers: 5);
        Assert.Equal(2d / 6, between?.Percentile);
        Assert.Equal(6, between?.OtherRunCount);
    }

    [Fact]
    public void Rank_UsesTheWeightsOfTheOthers()
    {
        EarningsYieldDto[] yields = [Yield(1m, weight: 3), Yield(2m), Yield(3m), Yield(4m), Yield(5m)];

        Assert.Equal(4d / 7, RunRank.Of(yields, 2.5m, null, isInDistribution: false, minimumOthers: 5)?.Percentile);
    }

    /// <summary>Five runs, one of them this one: four others are not enough to rank against.</summary>
    [Fact]
    public void Rank_WithFewerThanTheMinimumBesidesItself_IsNothing()
    {
        EarningsYieldDto[] yields = [.. Enumerable.Range(1, 5).Select(index => Yield(index * 1000m, "run-" + index))];

        Assert.Null(RunRank.Of(yields, 5000m, "run-5", isInDistribution: true, minimumOthers: 5));
        Assert.NotNull(RunRank.Of(yields, 5000m, "run-5", isInDistribution: false, minimumOthers: 5));
        Assert.Null(RunRank.Of([], 5000m, null, isInDistribution: false, minimumOthers: 5));
    }

    // ── The position ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARunThePolicyLeavesOut_HasNoRank_ButStillSitsOnTheScale()
    {
        RunPosition position = Position(2500m, [.. Many(6)], isCounted: false);

        Assert.Null(position.Rank);
        Assert.NotNull(position.Basis.Figures.Typical);
    }

    [Fact]
    public void WithoutATypicalRun_ThereIsNoRank()
    {
        Assert.Null(Position(2500m, [.. Many(3)]).Rank);
    }

    // ── The block ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The block is up at once and says it is looking; the figures land when the read does.</summary>
    [AvaloniaFact]
    public async Task TheBlock_IsLoadingAtOnce_AndFilledWhenTheReadLands()
    {
        var source = new ScriptedPositions();
        var viewModel = new RunPositionViewModel(source);

        Task shown = viewModel.ShowAsync("run", [(int)Pilot]);
        await source.WaitForReadAsync("run");

        Assert.True(viewModel.Earnings.IsLoading);
        Assert.False(shown.IsCompleted);
        Assert.False(viewModel.HasRank);

        source.Complete("run", Position(3500m, [.. Many(6)]));
        await shown;

        Assert.True(viewModel.Earnings.HasFigures);
        Assert.Equal("Better than 50% of runs", viewModel.RankText);
        Assert.StartsWith("ranked against 6 of your other runs · last 90 days", viewModel.RankDetailText);
        Assert.True(viewModel.Earnings.HasCurrent);
        Assert.Contains("THIS RUN", viewModel.Earnings.CurrentText);
    }

    [AvaloniaFact]
    public async Task ARunInTheTopQuarter_SaysSo()
    {
        var viewModel = new RunPositionViewModel(new ScriptedPositions().Returning("run", Position(9000m, [.. Many(8)])));

        await viewModel.ShowAsync("run", [(int)Pilot]);

        Assert.Equal("Better than 100% of runs · top 1%", viewModel.RankText);
    }

    /// <summary>A run above the range of the band gets the drop that made it, and the link to the loot table. Counter-proof:
    /// a run inside the band shows no drop, however valuable it is.</summary>
    [AvaloniaFact]
    public async Task AnOutlier_NamesItsNotableDrop_AndLinksToTheLootTable()
    {
        var opener = new RecordingLootTables();
        var drop = new ActivityTopDropDto { TypeId = 1, Name = "Gistii A-Type Shield Booster", Quantity = 3, ValueIsk = 15_000m, Rarity = LootRarityClass.Faction };
        var viewModel = new RunPositionViewModel(new ScriptedPositions().Returning("run", Position(20_000m, [.. Many(8)], drop: drop)), opener);

        await viewModel.ShowAsync("run", [(int)Pilot]);

        Assert.Equal("Above the range of half of runs", viewModel.OutlierText);
        Assert.Equal("Gistii A-Type Shield Booster ×3", viewModel.TopDropText);
        Assert.Equal("faction · 15k ISK · 75% of this run", viewModel.TopDropMetaText);
        Assert.True(viewModel.IsLootTableShown);

        viewModel.OpenLootTableCommand.Execute(null);

        (ActivityStatsKey? key, IReadOnlyList<int> characters) = Assert.Single(opener.Opened);
        Assert.Equal(Sanctum, key);
        Assert.Equal([(int)Pilot], characters);
    }

    [AvaloniaFact]
    public async Task ARunInsideTheBand_ShowsNoDrop()
    {
        var drop = new ActivityTopDropDto { TypeId = 1, Name = "Shield Booster", Quantity = 1, ValueIsk = 900m, Rarity = LootRarityClass.Common };
        var viewModel = new RunPositionViewModel(new ScriptedPositions().Returning("run", Position(4000m, [.. Many(8)], drop: drop)));

        await viewModel.ShowAsync("run", [(int)Pilot]);

        Assert.False(viewModel.HasOutlier);
        Assert.False(viewModel.HasTopDrop);
    }

    [AvaloniaFact]
    public async Task ARunBelowTheBand_IsSaidToBe_WithoutADrop()
    {
        var viewModel = new RunPositionViewModel(new ScriptedPositions().Returning("run", Position(100m, [.. Many(8)])));

        await viewModel.ShowAsync("run", [(int)Pilot]);

        Assert.Equal("Below the range of half of runs", viewModel.OutlierText);
        Assert.False(viewModel.HasTopDrop);
    }

    /// <summary>Under the minimum there is no number at all, the panel's own explanation instead, and no rank.</summary>
    [AvaloniaFact]
    public async Task UnderTheMinimum_ThereIsNoNumber_ButAnExplanation()
    {
        var viewModel = new RunPositionViewModel(new ScriptedPositions().Returning("run", Position(2500m, [.. Many(3)])));

        await viewModel.ShowAsync("run", [(int)Pilot]);

        Assert.True(viewModel.Earnings.IsTooFewRuns);
        Assert.Equal("Not enough runs yet — 3 of 5 needed", viewModel.Earnings.TooFewText);
        Assert.False(viewModel.HasRank);
        Assert.False(viewModel.Earnings.HasCurrent);
        Assert.Equal(string.Empty, viewModel.Earnings.TypicalText);
    }

    [AvaloniaFact]
    public async Task ARunThatDoesNotCount_SaysWhyItHasNoRank()
    {
        var viewModel = new RunPositionViewModel(new ScriptedPositions().Returning("run", Position(2500m, [.. Many(6)], isCounted: false)));

        await viewModel.ShowAsync("run", [(int)Pilot]);

        Assert.False(viewModel.HasRank);
        Assert.Contains("does not count", viewModel.RankNoteText);
        Assert.True(viewModel.Earnings.HasCurrent);
    }

    [AvaloniaFact]
    public async Task WithTooFewOtherRuns_TheRankSaysSo()
    {
        var viewModel = new RunPositionViewModel(new ScriptedPositions().Returning("run", Position(2500m, [.. Many(4)], selfInSamples: true)));

        await viewModel.ShowAsync("run", [(int)Pilot]);

        Assert.True(viewModel.Earnings.HasFigures);
        Assert.False(viewModel.HasRank);
        Assert.Equal("Not enough other runs to rank this one yet.", viewModel.RankNoteText);
    }

    /// <summary>The own runs stand in for the server with the reason the panel gives, and the basis line says whose runs.</summary>
    [AvaloniaFact]
    public async Task TheOwnRuns_AreSaidToBeTheFallback_WithTheReason()
    {
        RunPosition position = Position(2500m, [.. Many(6)], reason: OwnRunsReason.TooFewPilots);
        var viewModel = new RunPositionViewModel(new ScriptedPositions().Returning("run", position));

        await viewModel.ShowAsync("run", [(int)Pilot]);

        Assert.Equal(OwnRunsReasonText.For(OwnRunsReason.TooFewPilots), viewModel.Earnings.NoteText);
        Assert.Contains("of your runs", viewModel.Earnings.BasisText);
        Assert.Equal("YOUR RUNS", viewModel.Earnings.SourceChip);
    }

    [AvaloniaFact]
    public async Task AServersFigures_AreRankedWithTheirPilotsAndWindow()
    {
        RunPosition position = Position(2500m, [.. Many(6)], shared: true);
        var viewModel = new RunPositionViewModel(new ScriptedPositions().Returning("run", position));

        await viewModel.ShowAsync("run", [(int)Pilot]);

        Assert.StartsWith("ranked against 6 other runs · 3 pilots · last 90 days", viewModel.RankDetailText);
        Assert.Equal("ALL PILOTS · Krah Server", viewModel.Earnings.SourceChip);
    }

    /// <summary>Switching the source chip to another server's figures ranks the run among those.</summary>
    [AvaloniaFact]
    public async Task SwitchingServer_RanksTheRunAmongThatServersRuns()
    {
        RunPosition first = Position(3500m, [.. Many(6)], shared: true);
        RunPosition second = Position(3500m, [.. Many(6, offset: 3000m)], shared: true);
        RunPosition both = first with { Answers = [first.Basis, second.Basis with { ServerAddress = "https://other.invalid", ServerName = "Other" }] };
        var viewModel = new RunPositionViewModel(new ScriptedPositions().Returning("run", both));
        await viewModel.ShowAsync("run", [(int)Pilot]);
        Assert.Equal("Better than 50% of runs", viewModel.RankText);

        ServerOptionViewModel other = viewModel.Earnings.ServerOptions.Single(option => !option.IsCurrent);
        await other.ChooseCommand.ExecuteAsync(null);

        Assert.Equal("Better than 0% of runs", viewModel.RankText);
    }

    /// <summary>A late answer for the run the pilot already left never lands on the one they are looking at.</summary>
    [AvaloniaFact]
    public async Task ALateAnswer_NeverLandsOnTheNextRun()
    {
        var source = new ScriptedPositions();
        var viewModel = new RunPositionViewModel(source);
        Task first = viewModel.ShowAsync("first", [(int)Pilot]);
        await source.WaitForReadAsync("first");
        Task second = viewModel.ShowAsync("second", [(int)Pilot]);
        await source.WaitForReadAsync("second");

        source.Complete("second", Position(9000m, [.. Many(8)]));
        await second;
        source.Complete("first", Position(100m, [.. Many(8)]));
        await first;

        Assert.Equal("Better than 100% of runs · top 1%", viewModel.RankText);
    }

    [AvaloniaFact]
    public async Task AnActivityWithoutAKey_HidesTheBlock()
    {
        var viewModel = new RunPositionViewModel(new ScriptedPositions().Returning("run", null));

        await viewModel.ShowAsync("run", [(int)Pilot]);

        Assert.False(viewModel.Earnings.IsShown);
    }

    // ── The service: a real saved run against the real figures ──────────────────────────────────────────────

    /// <summary>Six saved runs worth 1,000 to 6,000: the best beats the five others (100 %), the third beats two of five
    /// (40 %), neither counted against itself. Counter-proof: leave the self-exclusion out and they read 91.7 % and 41.7 %.</summary>
    [AvaloniaFact]
    public async Task ASavedRun_IsRankedAmongTheOthers_NotAmongAllIncludingItself()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        Guid[] runs = await SeedAsync(instance, 6);
        IRunPositionSource source = instance.Services.GetRequiredService<IRunPositionSource>();

        RunPosition best = await ReadAsync(source, runs[5]);
        RunPosition third = await ReadAsync(source, runs[2]);

        Assert.Equal(1.0, best.Rank?.Percentile);
        Assert.Equal(5, best.Rank?.OtherRunCount);
        Assert.Equal(2d / 5, third.Rank?.Percentile);
        Assert.Equal(EarningsBasis.YourRuns, best.Basis.Basis);
        Assert.Equal(6000m, best.Run.YieldIsk);
    }

    /// <summary>Five saved runs are five runs including this one: the figures exist, a rank does not.</summary>
    [AvaloniaFact]
    public async Task FiveSavedRuns_GiveFigures_ButNoRank()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        Guid[] runs = await SeedAsync(instance, 5);

        RunPosition position = await ReadAsync(instance.Services.GetRequiredService<IRunPositionSource>(), runs[4]);

        Assert.NotNull(position.Basis.Figures.Typical);
        Assert.Null(position.Rank);
    }

    /// <summary>A second click on a run of the same activity is answered from memory; any change to a run drops it.
    /// Counter-proof: without the cache the second read asks again, without the invalidation the third does not.</summary>
    [AvaloniaFact]
    public async Task ThePosition_IsKeptBetweenClicks_AndDroppedWhenARunChanges()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        Guid[] runs = await SeedAsync(instance, 6);
        var stats = new CountingStats(instance.Services.GetRequiredService<IActivityStatsSource>());
        var source = new RunPositionService(instance.Services.GetRequiredService<IServiceScopeFactory>(), stats,
            instance.Services.GetRequiredService<ISharedServerPicks>(), instance.Services.GetRequiredService<IEventBus>());

        await ReadAsync(source, runs[5]);
        await ReadAsync(source, runs[5]);
        Assert.Equal(1, stats.Reads);

        await SaveRunAsync(instance, 7);
        await ReadAsync(source, runs[5]);
        Assert.Equal(2, stats.Reads);
    }

    /// <summary>The pilot's own figures are read once for as many runs of the activity as are clicked through.</summary>
    [AvaloniaFact]
    public async Task TheOwnFigures_AreReadOncePerActivity_UntilARunChanges()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        await SeedAsync(instance, 6);
        var own = new CountingOwnSource(instance.Services.GetRequiredService<IExpectedEarningsSource>());
        var service = new ActivityStatsService(instance.Services.GetRequiredService<IServiceScopeFactory>(), own,
            instance.Services.GetRequiredService<IServerRunOutcomeClient>(), instance.Services.GetRequiredService<IRemoteBusConnector>(),
            instance.Services.GetRequiredService<RunOutcomeSharingChoices>(), instance.Services.GetRequiredService<ISharedServerPicks>(),
            new OwnEarningsCache(instance.Services.GetRequiredService<IEventBus>()));

        await service.ReadKeyAsync(Sanctum, [(int)Pilot], Token);
        await service.ReadKeyAsync(Sanctum, [(int)Pilot], Token);
        Assert.Equal(1, own.Reads);

        await SaveRunAsync(instance, 7);
        await service.ReadKeyAsync(Sanctum, [(int)Pilot], Token);
        Assert.Equal(2, own.Reads);
    }

    /// <summary>The numbers asked for: the view-model path from click to a free UI, and from there to a filled block, cold,
    /// warm (the second run of the activity) and uncached (the cost every click had before the caches, forced by a change
    /// to a run before each). Logged, not asserted against a clock; what is asserted is that opening waits for nothing.</summary>
    [AvaloniaFact]
    public async Task TheBlock_NeverBlocksOpening_AndTheTimingsAreLogged()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        Guid[] runs = await SeedAsync(instance, 80);
        IRunPositionSource source = instance.Services.GetRequiredService<IRunPositionSource>();
        var viewModel = new RunPositionViewModel(source);

        (double Open, double Filled) cold = await TimeAsync(viewModel, runs[79]);
        double[] warm = [];
        for (int index = 0; index < 5; index++)
            warm = [.. warm, (await TimeAsync(viewModel, runs[index])).Filled];
        double[] uncached = [];
        for (int index = 5; index < 10; index++)
        {
            await instance.Services.GetRequiredService<IEventBus>().PublishAsync(new RunsChangedEvent(null, null), cancellationToken: Token);
            uncached = [.. uncached, (await TimeAsync(viewModel, runs[index])).Filled];
        }

        output.WriteLine($"click -> UI free (cold): {cold.Open:F1} ms; drawer -> block filled (cold): {cold.Filled:F1} ms");
        output.WriteLine($"drawer -> block filled, cached (5 clicks): {string.Join(", ", warm.Select(ms => ms.ToString("F1")))} ms");
        output.WriteLine($"drawer -> block filled, uncached (5 clicks): {string.Join(", ", uncached.Select(ms => ms.ToString("F1")))} ms");
        Assert.True(viewModel.Earnings.HasFigures);
        Assert.True(cold.Open < cold.Filled);
    }

    private static async Task<(double Open, double Filled)> TimeAsync(RunPositionViewModel viewModel, Guid run)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Task shown = viewModel.ShowAsync(run.ToString(), [(int)Pilot]);
        double open = watch.Elapsed.TotalMilliseconds;
        Assert.True(viewModel.Earnings.IsLoading || shown.IsCompleted);
        await shown;
        return (open, watch.Elapsed.TotalMilliseconds);
    }

    private static async Task<RunPosition> ReadAsync(IRunPositionSource source, Guid run) =>
        await source.ReadAsync(run.ToString(), [(int)Pilot], Token) ?? throw new Xunit.Sdk.XunitException("the saved run has no position");

    private static async Task<Guid[]> SeedAsync(TestClientInstance instance, int count)
    {
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = Tritanium, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }], Token);
        Guid[] runs = new Guid[count];
        for (int index = 0; index < count; index++)
            runs[index] = await SaveRunAsync(instance, index + 1);
        return runs;
    }

    // Run n brings n * 10 Tritanium at 100 ISK: 1,000 ISK for the first, 6,000 for the sixth.
    private static async Task<Guid> SaveRunAsync(TestClientInstance instance, int number)
    {
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        DateTime startedAtUtc = DateTime.UtcNow.Date.AddDays(-number).AddHours(10);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Site, startedAtUtc, SiteTypeId,
            "Angel Sanctum", 30000142), Token);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15), startedAtUtc.AddMinutes(16),
            [
                new RunLootCaptureInput
                {
                    CapturedAtUtc = startedAtUtc.AddMinutes(10), Source = LootCaptureSource.Clipboard, ContentHash = $"{startedAtUtc:O}",
                    Entries = [new RunLootEntryInput { ItemTypeId = Tritanium, Name = "Tritanium", Quantity = number * 10, LootKind = LootKind.Gained }]
                }
            ], [], [], []), Token);
        return started.Value;
    }

    // ── Builders and doubles ────────────────────────────────────────────────────────────────────────────────

    private static EarningsYieldDto Yield(decimal isk, string? activityRef = null, double weight = 1) =>
        new() { YieldIsk = isk, Weight = weight, ActivityRef = activityRef };

    internal static IEnumerable<decimal> Many(int count, decimal offset = 0) =>
        Enumerable.Range(1, count).Select(index => offset + index * 1000m);

    /// <summary>A run of <paramref name="runYield"/> against the other runs: the figures are made the way the app makes them,
    /// from samples, and the run is one of them (under its reference) when <paramref name="selfInSamples"/> says so.</summary>
    internal static RunPosition Position(decimal runYield, IReadOnlyList<decimal> others, bool isCounted = true, bool selfInSamples = false,
        ActivityTopDropDto? drop = null, OwnRunsReason reason = OwnRunsReason.None, bool shared = false)
    {
        DateTime now = DateTime.UtcNow;
        List<EarningsSample> samples = [.. others.Select((isk, index) => Sample(isk, now.AddDays(-index - 1), null))];
        if (selfInSamples)
            samples.Add(Sample(runYield, now.AddHours(-1), "run"));

        EarningsFiguresDto figures = EarningsStatistics.Compute(samples, _ => null, EarningsPolicy.For(ActivityStatsKind.Site), now);
        ExpectedEarnings basis = shared
            ? new ExpectedEarnings(EarningsBasis.AllPilots, figures, 3, "Krah Server", "https://krah.invalid")
            : new ExpectedEarnings(EarningsBasis.YourRuns, figures, 1);
        var run = new ActivityYieldDto
        {
            Key = Sanctum, YieldIsk = runYield, CompletedAtUtc = now.AddHours(-1), IsCounted = isCounted, TopDrop = drop
        };
        return RunPosition.From("run", run, basis, [basis], reason);
    }

    private static EarningsSample Sample(decimal isk, DateTime completedAtUtc, string? activityRef) => new()
    {
        PilotBucket = "pilot", CapWeight = 1, CompletedAtUtc = completedAtUtc, DurationSeconds = 900, FixedIsk = isk,
        LootBasis = LootBasis.CargoDifference, ActivityRef = activityRef
    };

    private sealed class ScriptedPositions : IRunPositionSource
    {
        private readonly Dictionary<string, TaskCompletionSource<RunPosition?>> _reads = [];
        private readonly Dictionary<string, TaskCompletionSource> _asked = [];

        public ScriptedPositions Returning(string activityRef, RunPosition? position)
        {
            Read(activityRef).SetResult(position);
            return this;
        }

        public void Complete(string activityRef, RunPosition? position) => Read(activityRef).SetResult(position);

        public Task WaitForReadAsync(string activityRef) => Asked(activityRef).Task;

        public Task<RunPosition?> ReadAsync(string activityRef, IReadOnlyList<int> characterIds, CancellationToken cancellationToken = default)
        {
            Asked(activityRef).TrySetResult();
            return Read(activityRef).Task;
        }

        public Task<IReadOnlyDictionary<string, RunVsTypical>> ReadVsTypicalAsync(IReadOnlyCollection<string> activityRefs,
            IReadOnlyList<int> characterIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, RunVsTypical>>(new Dictionary<string, RunVsTypical>());

        private TaskCompletionSource<RunPosition?> Read(string activityRef) =>
            _reads.TryGetValue(activityRef, out TaskCompletionSource<RunPosition?>? read)
                ? read
                : _reads[activityRef] = new TaskCompletionSource<RunPosition?>(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource Asked(string activityRef) =>
            _asked.TryGetValue(activityRef, out TaskCompletionSource? asked)
                ? asked
                : _asked[activityRef] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecordingLootTables : ILootTableOpener
    {
        public List<(ActivityStatsKey? Key, IReadOnlyList<int> Characters)> Opened { get; } = [];

        public void Open(ActivityStatsKey? key, IReadOnlyList<int> characterIds) => Opened.Add((key, characterIds));
    }

    private sealed class CountingStats(IActivityStatsSource inner) : IActivityStatsSource
    {
        public int Reads { get; private set; }

        public Task<ActivityStatsOverview> ReadAsync(ActivityStatsRequest request, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(request, cancellationToken);

        public Task<ActivityStats> ReadKeyAsync(ActivityStatsKey key, IReadOnlyList<int> characterIds, CancellationToken cancellationToken = default)
        {
            Reads++;
            return inner.ReadKeyAsync(key, characterIds, cancellationToken);
        }
    }

    private sealed class CountingOwnSource(IExpectedEarningsSource inner) : IExpectedEarningsSource
    {
        public int Reads { get; private set; }

        public Task<ExpectedEarnings> LoadOwnAsync(ActivityStatsKey key, CancellationToken cancellationToken = default)
        {
            Reads++;
            return inner.LoadOwnAsync(key, cancellationToken);
        }

        public Task<SharedEarningsAttempt> LoadSharedAsync(ActivityStatsKey key, int characterId, CancellationToken cancellationToken = default,
            string? activityRef = null, string? onlyServer = null) =>
            inner.LoadSharedAsync(key, characterId, cancellationToken, activityRef, onlyServer);
    }

    /// <summary>Hands every read the same position, for a screen test that is about the layout and not about the figures.</summary>
    internal sealed class FixedPosition(RunPosition? position, IReadOnlyDictionary<string, RunVsTypical>? vsTypical = null) : IRunPositionSource
    {
        public Task<RunPosition?> ReadAsync(string activityRef, IReadOnlyList<int> characterIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(position);

        public Task<IReadOnlyDictionary<string, RunVsTypical>> ReadVsTypicalAsync(IReadOnlyCollection<string> activityRefs,
            IReadOnlyList<int> characterIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(vsTypical ?? new Dictionary<string, RunVsTypical>());
    }
}
