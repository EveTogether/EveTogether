using Avalonia.Headless.XUnit;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-160: the read path for a saved run. One counter-proof per acceptance criterion, taken from the
/// ticket itself.</summary>
public sealed class ActivityQueriesTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>AC-1: six saved runs sharing a group code are one activity, not six. Counter-proof (design-time,
    /// not re-run here): a query that counted rows instead of reading <c>RunsIncluded</c> would see six, and a
    /// query that grouped over <c>Run</c> directly instead of <c>ActivitySummary</c> would return six rows.</summary>
    [AvaloniaFact]
    public async Task Overview_SixRunsUnderOneGroupCode_IsOneRowWithSixIncluded()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        for (long characterId = 90000001; characterId <= 90000006; characterId++)
            await _SaveRunAsync(dispatcher, characterId, "HF-7QK2", cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);

        ActivityOverviewRowDto row = Assert.Single(_Value(overview));
        Assert.Equal(6, row.RunsIncluded);
        Assert.Equal(6, row.ParticipantCount);
    }

    /// <summary>AC-2: a solo run (no group code) travels the same query, same shape — one row, one included run.
    /// Counter-proof: an <c>if (GroupCode is null)</c> branch in the handler would be the tell; there is none.</summary>
    [AvaloniaFact]
    public async Task Overview_SoloRun_IsOneRowWithOneIncluded()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveRunAsync(dispatcher, 90000001, groupCode: null, cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);

        ActivityOverviewRowDto row = Assert.Single(_Value(overview));
        Assert.Equal(1, row.RunsIncluded);
    }

    /// <summary>AC-3, first counter-proof: a deleted run's activity does not show up. Drop the
    /// <c>DeletedAtUtc</c> filter and this run would appear.</summary>
    [AvaloniaFact]
    public async Task Overview_DeletedRun_DoesNotAppear()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        await dispatcher.Send(new DeleteRunCommand(started.Value, StartedAtUtc.AddMinutes(20)), cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);

        Assert.Empty(_Value(overview));
    }

    /// <summary>AC-3, second counter-proof: a discarded run — unlinked, not deleted — still shows up, as its own
    /// activity. Filtering discarded runs out too would make this go empty as well.</summary>
    [AvaloniaFact]
    public async Task Overview_DiscardedRun_StillAppearsAsItsOwnActivity()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
        await dispatcher.Send(new DiscardRunCommand(started.Value, StartedAtUtc.AddMinutes(16)), cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        Result<IReadOnlyList<ActivityOverviewRowDto>> overview =
            await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);

        ActivityOverviewRowDto row = Assert.Single(_Value(overview));
        Assert.Null(row.GroupCode);
    }

    /// <summary>AC-4: the detail of run A shows only A's loot, even while a third run is running and B carries
    /// different loot. Counter-proof: routing the detail through <c>GetRunningRunLootQuery</c> would show C's loot
    /// (wrong run) while C is running, or refuse outright once it stops.</summary>
    [AvaloniaFact]
    public async Task Detail_ShowsOnlyItsOwnRunsLoot_EvenWithAnotherRunRunning()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> runA = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(runA.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [new RunLootCaptureInput
            {
                CapturedAtUtc = StartedAtUtc.AddMinutes(10), Source = LootCaptureSource.Clipboard,
                Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 1, LootKind = LootKind.Gained }]
            }], [], [], []), cancellationToken);
        Result<Guid> runB = await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(runB.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [new RunLootCaptureInput
            {
                CapturedAtUtc = StartedAtUtc.AddMinutes(10), Source = LootCaptureSource.Clipboard,
                Entries = [new RunLootEntryInput { ItemTypeId = 35, Name = "Pyerite", Quantity = 1, LootKind = LootKind.Gained }]
            }], [], [], []), cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        // A third run is running right now — this is what GetRunningRunLootQuery would answer with instead.
        await dispatcher.Send(new StartRunCommand(90000003, ActivityKind.Site, StartedAtUtc, 1234, "Homefront", 30000142), cancellationToken);

        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        ActivityOverviewRowDto rowA = _Value(overview).Single(row => row.RunId == runA.Value);
        Result<ActivityDetailDto> detail = await dispatcher.Query(new GetActivityDetailQuery(rowA.ActivitySummaryId), cancellationToken);

        ActivityRunDetailDto runDetail = Assert.Single(_Value(detail).Runs);
        RunLootCaptureDto capture = Assert.Single(runDetail.LootCaptures);
        RunLootEntryDto entry = Assert.Single(capture.Entries);
        Assert.Equal(34, entry.ItemTypeId);
    }

    /// <summary>AC-5, applied to the current model (ET-115 merged the old per-direction storage away — the code
    /// comment on <c>RunEnemyObservationCollector.Record</c> spells that out): two runs in the same activity that
    /// each sighted the same enemy type stay two rows. Counter-proof: group by <c>EnemyTypeId</c> alone across the
    /// activity's runs and the later observation silently overwrites the earlier one, leaving a single row.</summary>
    [AvaloniaFact]
    public async Task Detail_SameEnemyTypeSeenOnTwoRunsInOneActivity_StaysTwoRows()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> first = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(first.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [], [],
            [new RunEnemyObservationInput { Count = 2, EnemyTypeId = 111, EnemyName = "Raid Leader", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc.AddMinutes(1) }], []), cancellationToken);
        Result<Guid> second = await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(second.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [], [],
            [new RunEnemyObservationInput { Count = 3, EnemyTypeId = 111, EnemyName = "Raid Leader", FirstObservedAtUtc = StartedAtUtc.AddMinutes(2), LastObservedAtUtc = StartedAtUtc.AddMinutes(3) }], []), cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(overview));
        Result<ActivityDetailDto> detail = await dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId), cancellationToken);

        Assert.Equal(2, _Value(detail).EnemyObservations.Count);
        Assert.Equal(5, _Value(detail).EnemyObservations.Sum(observation => observation.Count));
    }

    /// <summary>AC-6: an excluded capture is still returned, flagged, and the activity's totals — read from
    /// <c>ActivitySummary</c> — do not include it. Counter-proof: filtering excluded captures out of the result set
    /// would make the capture disappear instead of just not count.</summary>
    [AvaloniaFact]
    public async Task Detail_ExcludedCapture_StaysVisibleButIsNotInTheTotals()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [new LocalMarketPrice { TypeId = 34, AveragePrice = 100, AdjustedPrice = 100, UpdatedAt = DateTimeOffset.UtcNow }], cancellationToken);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        RunLootCaptureInput CaptureAt(int minute) => new()
        {
            CapturedAtUtc = StartedAtUtc.AddMinutes(minute), Source = LootCaptureSource.Clipboard, ContentHash = "ABC",
            Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = 3, LootKind = LootKind.Gained }]
        };
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
            [CaptureAt(10), CaptureAt(11)], [], [], []), cancellationToken);
        Result<RunLootOverview> loot = await dispatcher.Query(new GetRunLootQuery(started.Value), cancellationToken);
        RunLootCaptureDto repeat = _Value(loot).Captures.OrderBy(capture => capture.CapturedAtUtc).Last();
        await dispatcher.Send(new SetRunLootCaptureExclusionCommand(repeat.CaptureId, IsExcluded: true), cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);
        ActivityOverviewRowDto row = Assert.Single(_Value(overview));
        Result<ActivityDetailDto> detail = await dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId), cancellationToken);

        ActivityRunDetailDto runDetail = Assert.Single(_Value(detail).Runs);
        Assert.Equal(2, runDetail.LootCaptures.Count);
        Assert.Contains(runDetail.LootCaptures, capture => capture.IsExcluded);
        Assert.Equal(300m, _Value(detail).LootIskGained);   // 100 × 3, the excluded repeat's 3 more do not count
    }

    /// <summary>AC-7: rewards come back per kind, never summed to one figure, and a kind the query does not
    /// special-case does not vanish. Counter-proof: summing all posts would collapse three into one figure, and
    /// hard-coding the known keys would silently drop <c>Filament</c>.</summary>
    [AvaloniaFact]
    public async Task Overview_RewardsComeBackPerKind_NotSummed_AndAnUnhandledKeySurvives()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [], [], [],
            [
                new RunParameterInput { ParameterKey = RunParameterKey.Isk, TypedValue = "1,000,000", Amount = 1_000_000m, ObservedAtUtc = StartedAtUtc.AddMinutes(1) },
                new RunParameterInput { ParameterKey = RunParameterKey.LoyaltyPoints, TypedValue = "1,240", Amount = 1_240m, ObservedAtUtc = StartedAtUtc.AddMinutes(1) },
                new RunParameterInput { ParameterKey = RunParameterKey.Evermarks, TypedValue = "12", Amount = 12m, ObservedAtUtc = StartedAtUtc.AddMinutes(1) },
                new RunParameterInput { ParameterKey = RunParameterKey.Filament, TypedValue = "Gravid Filament", ObservedAtUtc = StartedAtUtc.AddMinutes(1) }
            ]), cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken);

        ActivityOverviewRowDto row = Assert.Single(_Value(overview));
        Assert.Equal(4, row.Rewards.Count);
        Assert.Equal(1_000_000m, row.Rewards.Single(reward => reward.ParameterKey == RunParameterKey.Isk).Amount);
        Assert.Equal(1_240m, row.Rewards.Single(reward => reward.ParameterKey == RunParameterKey.LoyaltyPoints).Amount);
        Assert.Equal(12m, row.Rewards.Single(reward => reward.ParameterKey == RunParameterKey.Evermarks).Amount);
        Assert.Contains(row.Rewards, reward => reward.ParameterKey == RunParameterKey.Filament);   // did not silently vanish
    }

    [AvaloniaFact]
    public async Task Detail_ObservedAndCountedEnemies_PreserveTheirCounts()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [], [],
            [
                new RunEnemyObservationInput { Count = 3, EnemyTypeId = 111, EnemyName = "Centii Scavenger", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc },
                new RunEnemyObservationInput { Count = 0, EnemyTypeId = 112, EnemyName = "Centii Loyalist", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc }
            ], []), cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        ActivityOverviewRowDto row = Assert.Single(_Value(await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken)));
        ActivityDetailDto detail = _Value(await dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId), cancellationToken));

        Assert.Equal(2, detail.EnemyObservations.Count);
        Assert.Contains(detail.EnemyObservations, observation => observation.EnemyTypeId == 111 && observation.Count == 3);
        Assert.Contains(detail.EnemyObservations, observation => observation.EnemyTypeId == 112 && observation.Count == 0);
    }

    [AvaloniaTheory]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    public async Task Overview_ObservedEnemyTypes_CountsDistinctTypes(bool repeatsAnEnemyType, int expectedEnemyTypeCount)
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [], [],
            [
                new RunEnemyObservationInput { Count = 4, EnemyTypeId = 111, EnemyName = "Centii Scavenger", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc },
                new RunEnemyObservationInput { Count = 0, EnemyTypeId = 112, EnemyName = "Centii Loyalist", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc },
                new RunEnemyObservationInput { Count = 0, EnemyTypeId = repeatsAnEnemyType ? 111 : 113, EnemyName = "Centii Enslaver", FirstObservedAtUtc = StartedAtUtc, LastObservedAtUtc = StartedAtUtc }
            ], []), cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        ActivityOverviewRowDto row = Assert.Single(_Value(await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken)));

        Assert.Equal(expectedEnemyTypeCount, row.EnemyTypeCount);
    }

    /// <summary>ET-233: <c>ActivityDetailViewModel._FindAgainAsync</c> now looks an activity back up by its own key
    /// (group code, or run id when it never had one) straight off <c>ActivitySummary</c>, rather than through the
    /// runs overview — which is bounded to whichever month is on screen (this same ticket) and would miss an
    /// activity outside it regardless of how many others exist. Fifty-five newer activities besides the target one
    /// pin that the lookup is a real key match, not an accident of sort order or of however many rows happen to come
    /// back first. Counter-proof: swap the <c>RunId</c> branch for the <c>GroupCode</c> one (or drop the query
    /// entirely) and this goes red on a null result instead of the target's own id.</summary>
    [AvaloniaFact]
    public async Task FindActivitySummaryIdQuery_FindsAnActivityAmongManyOthers()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
        for (int index = 1; index <= 55; index++)
        {
            DateTime newerStartedAtUtc = StartedAtUtc.AddMonths(index);
            Result<Guid> newer = await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Site,
                newerStartedAtUtc, 1234, "Homefront", 30000142), cancellationToken);
            await dispatcher.Send(new SaveRunCommand(newer.Value, newerStartedAtUtc.AddMinutes(15),
                newerStartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
        }

        Result<Guid?> found = await dispatcher.Query(new FindActivitySummaryIdQuery(null, started.Value), cancellationToken);

        Assert.True(found.IsSuccess);
        ActivityOverviewRowDto row = _Value(await dispatcher.Query(new GetActivityOverviewQuery(), cancellationToken))
            .Single(candidate => candidate.RunId == started.Value);
        Assert.Equal(row.ActivitySummaryId, found.Value);
    }

    private static async Task _SaveRunAsync(IDispatcher dispatcher, long characterId, string? groupCode, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, groupCode), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
    }

    private static T _Value<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Messages[0].Text);
        return result.Value!;
    }
}
