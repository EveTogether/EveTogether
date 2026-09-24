using System.Text.Json;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails;
using EveUtils.Shared.Modules.Killmails.Commands;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Enums;
using EveUtils.Shared.Modules.Killmails.Events;
using EveUtils.Shared.Modules.Killmails.Repositories;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-331: an own loss linked to the run it happened in, FAILED / SHIP LOST derived from it, and its cost in TOTAL ISK
/// on every screen while nothing of it travels with a published run. One test per acceptance criterion.
/// </summary>
public sealed class KillmailRunLinkTests
{
    private const int Pilot = 90000001;
    private const int KSpace = 30000142;
    private const int Abyss = 32000001;
    private const int Rifter = 587;
    private const int Slasher = 585;
    private const int Capsule = 670;
    private const int Tritanium = 34;
    private const int Hobgoblin = 2488;
    private const int Filament = 60000;
    private static readonly DateTime StartedAtUtc = new(2026, 9, 20, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime StoppedAtUtc = StartedAtUtc.AddMinutes(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Criterion 1. Red if the rule links outside the run's stop plus two minutes.</summary>
    [Theory]
    [InlineData(60, KillmailLinkSource.Auto)]
    [InlineData(180, KillmailLinkSource.None)]
    public async Task Link_TheOneFittingRun_IsAuto_OnlyUpToTwoMinutesAfterItsStop(int secondsAfterStop, KillmailLinkSource expected)
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _SaveRunAsync(dispatcher, ActivityKind.Site, KSpace, StartedAtUtc);
        await _AddAsync(instance, _Loss(1, StoppedAtUtc.AddSeconds(secondsAfterStop), KSpace, Rifter));

        await dispatcher.Send(new LinkKillmailsToRunsCommand(Pilot), Ct);

        Assert.Equal(expected, (await _StoredAsync(instance, 1)).LinkSource);
    }

    /// <summary>Criterion 2. Red if the first candidate is chosen.</summary>
    [Fact]
    public void Match_TwoFittingRuns_LinksNeither_AndCountsBoth()
    {
        KillmailRunMatch match = KillmailRunLinker.Match(_Loss(1, StartedAtUtc.AddMinutes(5), KSpace, Rifter),
            [_Run(ActivityKind.Site, null), _Run(ActivityKind.Site, null)], [], StoppedAtUtc);

        Assert.Equal(new KillmailRunMatch(null, 2), match);
    }

    /// <summary>Criterion 3. Red without the ActivityKind filter: a site run with no system would pass for the abyss.</summary>
    [Theory]
    [InlineData(ActivityKind.Site, 0)]
    [InlineData(ActivityKind.Abyssal, 1)]
    public void Match_AnAbyssalLoss_FitsOnlyAnAbyssalRun(ActivityKind kind, int expectedCandidates)
    {
        KillmailRunMatch match = KillmailRunLinker.Match(_Loss(1, StartedAtUtc.AddMinutes(5), Abyss, Rifter),
            [_Run(kind, null)], [], StoppedAtUtc);

        Assert.Equal(expectedCandidates, match.CandidateCount);
    }

    /// <summary>Criterion 4. Red if the lost hull is not compared with the run's fit.</summary>
    [Theory]
    [InlineData(Rifter, 1)]
    [InlineData(Slasher, 0)]
    public void Match_TheFitsHull_MustBeTheLostShip(int fitHull, int expectedCandidates)
    {
        KillmailRunMatch match = KillmailRunLinker.Match(_Loss(1, StartedAtUtc.AddMinutes(5), KSpace, Rifter),
            [_Run(ActivityKind.Site, fitHull)], [], StoppedAtUtc);

        Assert.Equal(expectedCandidates, match.CandidateCount);
    }

    /// <summary>Criterion 5. The pod, lost past the ship rule's two minutes, follows its ship's run only by the pod rule;
    /// red if a pod-only loss makes its run lose a ship.</summary>
    [Fact]
    public async Task Link_ThePodFollowsItsShip_AndOnlyTheShipMakesTheRunLoseIt()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid shipRun = await _SaveRunAsync(dispatcher, ActivityKind.Site, KSpace, StartedAtUtc);
        Guid podRun = await _SaveRunAsync(dispatcher, ActivityKind.Site, KSpace, StartedAtUtc.AddHours(1));
        await _AddAsync(instance,
            _Loss(1, StoppedAtUtc.AddSeconds(100), KSpace, Rifter),
            _Loss(2, StoppedAtUtc.AddSeconds(130), KSpace, Capsule),
            _Loss(3, StartedAtUtc.AddHours(1).AddMinutes(10), KSpace, Capsule));

        await dispatcher.Send(new LinkKillmailsToRunsCommand(Pilot), Ct);
        await dispatcher.Send(new SetKillmailRunLinkCommand(Pilot, 3, podRun), Ct);

        IReadOnlyList<ActivityOverviewRowDto> rows = await _RowsAsync(dispatcher);
        Assert.Equal(shipRun, (await _StoredAsync(instance, 2)).RunId);
        Assert.True(rows.Single(row => row.RunId == shipRun).HasShipLoss);
        Assert.False(rows.Single(row => row.RunId == podRun).HasShipLoss);
    }

    /// <summary>Criterion 6. Red if the automatic rule overwrites the pilot's own unlink.</summary>
    [Fact]
    public async Task Link_LeavesAManualUnlinkAlone()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _SaveRunAsync(dispatcher, ActivityKind.Site, KSpace, StartedAtUtc);
        await _AddAsync(instance, _Loss(1, StartedAtUtc.AddMinutes(5), KSpace, Rifter));
        await dispatcher.Send(new SetKillmailRunLinkCommand(Pilot, 1, null), Ct);

        await dispatcher.Send(new LinkKillmailsToRunsCommand(Pilot), Ct);

        LocalKillmail stored = await _StoredAsync(instance, 1);
        Assert.Equal<(Guid?, KillmailLinkSource)>((null, KillmailLinkSource.Manual), (stored.RunId, stored.LinkSource));
    }

    /// <summary>Criterion 7. Red if the ship loss were stored on the activity: it would outlive the unlink.</summary>
    [Fact]
    public async Task Unlink_TakesTheShipLossAndItsIskAway()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _PriceAsync(instance, (Rifter, 300_000));
        await _SaveRunAsync(dispatcher, ActivityKind.Abyssal, Abyss, StartedAtUtc);
        await _AddAsync(instance, _Loss(1, StartedAtUtc.AddMinutes(5), Abyss, Rifter));
        await dispatcher.Send(new LinkKillmailsToRunsCommand(Pilot), Ct);
        ActivityOverviewRowDto linked = Assert.Single(await _RowsAsync(dispatcher));

        await dispatcher.Send(new SetKillmailRunLinkCommand(Pilot, 1, null), Ct);

        ActivityOverviewRowDto unlinked = Assert.Single(await _RowsAsync(dispatcher));
        Assert.Equal((true, -300_000m), (linked.HasShipLoss, linked.Isk.Of(IskSource.ShipLoss)?.Amount));
        Assert.Equal((false, (decimal?)null), (unlinked.HasShipLoss, unlinked.Isk.Of(IskSource.ShipLoss)?.Amount));
    }

    /// <summary>Criterion 8: loot + bounty − filament − (hull + destroyed + dropped) at the average price, the same on
    /// the overview row, the detail screen and the day total. Red if the loss counts on only one of them.</summary>
    [AvaloniaFact]
    public async Task TotalIsk_TakesTheLossOff_OnTheRowTheDetailAndTheDay()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _PriceAsync(instance, (Tritanium, 1_000), (Filament, 100_000), (Rifter, 300_000), (Hobgoblin, 10_000));
        await _SaveRunAsync(dispatcher, ActivityKind.Abyssal, Abyss, StartedAtUtc,
            [
                new RunLootCaptureInput
                {
                    CapturedAtUtc = StartedAtUtc.AddMinutes(10), Source = LootCaptureSource.Clipboard, ContentHash = "loot",
                    Entries = [new RunLootEntryInput { ItemTypeId = Tritanium, Name = "Tritanium", Quantity = 1_000, LootKind = LootKind.Gained }]
                }
            ],
            [new RunBountyEntryInput { OccurredAtUtc = StartedAtUtc.AddMinutes(3), Isk = 500_000m }],
            [
                new RunParameterInput { ParameterKey = RunParameterKey.AbyssalFilamentTypeId, TypedValue = "60000", ObservedAtUtc = StartedAtUtc },
                new RunParameterInput { ParameterKey = RunParameterKey.AbyssalFilamentCount, TypedValue = "2", ObservedAtUtc = StartedAtUtc }
            ]);
        await _AddAsync(instance, _Loss(1, StartedAtUtc.AddMinutes(5), Abyss, Rifter,
            new LocalKillmailItem { Flag = 87, TypeId = Hobgoblin, QuantityDestroyed = 2, QuantityDropped = 1 }));

        await dispatcher.Send(new LinkKillmailsToRunsCommand(Pilot), Ct);

        ActivityOverviewRowDto row = Assert.Single(await _RowsAsync(dispatcher));
        Result<ActivityDetailDto> detail = await dispatcher.Query(new GetActivityDetailQuery(row.ActivitySummaryId), Ct);
        decimal day = RunsActivitySummaryText.SourcesFor(
            [new ActivityOverviewRowViewModel(row, id => $"character {id}", _ => Task.CompletedTask, _ => Task.CompletedTask)]).Total;
        // 1,000 × 1,000 loot + 500,000 bounty − 2 × 100,000 filament − (300,000 + 3 × 10,000) lost
        Assert.Equal((970_000m, 970_000m, 970_000m), (row.Isk.Total, detail.Value?.Isk.Total, day));
    }

    /// <summary>Criterion 9. Red if linking touches the run itself, such as its revision or sync state, or if anything
    /// of the killmail is added to what travels to a server.</summary>
    [Fact]
    public async Task WireData_OfARunWithALinkedLoss_IsTheSameAsWithout()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _SaveRunAsync(dispatcher, ActivityKind.Abyssal, Abyss, StartedAtUtc);
        string before = await _WireAsync(instance, runId);
        await _AddAsync(instance, _Loss(1, StartedAtUtc.AddMinutes(5), Abyss, Rifter));

        await dispatcher.Send(new LinkKillmailsToRunsCommand(Pilot), Ct);

        Assert.Equal(runId, (await _StoredAsync(instance, 1)).RunId);
        Assert.Equal(before, await _WireAsync(instance, runId));
    }

    /// <summary>ET-382. Red if the automatic link stays silent: an open killmail screen would keep showing the loss as
    /// not linked. The second pass changes nothing and so says nothing.</summary>
    [Fact]
    public async Task Link_PublishesKillmailsChanged_OnlyWhenALossChanged()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _SaveRunAsync(dispatcher, ActivityKind.Site, KSpace, StartedAtUtc);
        await _AddAsync(instance, _Loss(1, StartedAtUtc.AddMinutes(5), KSpace, Rifter));
        List<KillmailsChangedEvent> heard = [];
        instance.Services.GetRequiredService<IEventBus>().Subscribe<KillmailsChangedEvent>(published => heard.Add(published));

        await dispatcher.Send(new LinkKillmailsToRunsCommand(Pilot), Ct);
        await dispatcher.Send(new LinkKillmailsToRunsCommand(Pilot), Ct);

        KillmailsChangedEvent change = Assert.Single(heard);
        Assert.Equal((Pilot, KillmailsChangeKind.RunLinkChanged), (change.Data.CharacterId, change.Data.Kind));
    }

    /// <summary>ET-382. Red if a manual link or unlink stays silent to the screens that did not make it.</summary>
    [Fact]
    public async Task SetLink_PublishesKillmailsChanged()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _SaveRunAsync(dispatcher, ActivityKind.Site, KSpace, StartedAtUtc);
        await _AddAsync(instance, _Loss(1, StartedAtUtc.AddMinutes(5), KSpace, Rifter));
        List<KillmailsChangedEvent> heard = [];
        instance.Services.GetRequiredService<IEventBus>().Subscribe<KillmailsChangedEvent>(published => heard.Add(published));

        await dispatcher.Send(new SetKillmailRunLinkCommand(Pilot, 1, runId), Ct);

        Assert.Equal(Pilot, Assert.Single(heard).Data.CharacterId);
    }

    private static LinkableRun _Run(ActivityKind kind, int? fitHull) =>
        new(Guid.NewGuid(), Pilot, kind, null, StartedAtUtc, StoppedAtUtc, fitHull);

    private static LocalKillmail _Loss(int killmailId, DateTime atUtc, int solarSystemId, int shipTypeId,
        params LocalKillmailItem[] items) => new()
    {
        CharacterId = Pilot,
        KillmailId = killmailId,
        Hash = $"hash{killmailId}",
        KillmailTimeUtc = atUtc,
        SolarSystemId = solarSystemId,
        IsLoss = true,
        VictimShipTypeId = shipTypeId,
        VictimCharacterId = Pilot,
        LinkSource = KillmailLinkSource.None,
        ImportedAtUtc = DateTime.UtcNow,
        Items = [.. items]
    };

    private static Task _AddAsync(TestClientInstance instance, params LocalKillmail[] losses) =>
        instance.Services.GetRequiredService<ILocalKillmailRepository>().AddMissingAsync(Pilot, losses, Ct);

    private static async Task<LocalKillmail> _StoredAsync(TestClientInstance instance, int killmailId) =>
        (await instance.Services.GetRequiredService<ILocalKillmailRepository>().GetForCharacterAsync(Pilot, Ct))
        .Single(killmail => killmail.KillmailId == killmailId);

    private static async Task<IReadOnlyList<ActivityOverviewRowDto>> _RowsAsync(IDispatcher dispatcher) =>
        (await dispatcher.Query(new GetActivityOverviewQuery(), Ct)).Value ?? [];

    private static Task _PriceAsync(TestClientInstance instance, params (int TypeId, double Price)[] prices) =>
        instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            .. prices.Select(price => new LocalMarketPrice
            {
                TypeId = price.TypeId, AveragePrice = price.Price, AdjustedPrice = price.Price, UpdatedAt = DateTimeOffset.UtcNow
            })
        ], Ct);

    private static Task<Guid> _SaveRunAsync(IDispatcher dispatcher, ActivityKind kind, int solarSystemId,
        DateTime startedAtUtc) =>
        _SaveRunAsync(dispatcher, kind, solarSystemId, startedAtUtc, [], [], []);

    private static async Task<Guid> _SaveRunAsync(IDispatcher dispatcher, ActivityKind kind, int solarSystemId,
        DateTime startedAtUtc, RunLootCaptureInput[] loot, RunBountyEntryInput[] bounty, RunParameterInput[] parameters)
    {
        Result<Guid> started = await dispatcher.Send(
            new StartRunCommand(Pilot, kind, startedAtUtc, 0, null, solarSystemId), Ct);
        Result saved = await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(20),
            startedAtUtc.AddMinutes(21), loot, bounty, [], parameters), Ct);
        Assert.True(saved.IsSuccess);
        return started.Value;
    }

    private static async Task<string> _WireAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Ct);
        Run run = await db.Set<Run>().AsNoTracking()
            .Include(stored => stored.LootCaptures).ThenInclude(capture => capture.Entries)
            .Include(stored => stored.BountyEntries)
            .Include(stored => stored.EnemyObservations)
            .Include(stored => stored.Parameters)
            .Include(stored => stored.MiningEntries)
            .Include(stored => stored.AttendanceEntries)
            .SingleAsync(stored => stored.Id == runId, Ct);
        return JsonSerializer.Serialize(RunWireData.FromEntity(run));
    }
}
