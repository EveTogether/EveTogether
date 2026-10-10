using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-460: an item name no SDE type carries yet (a new event item) is kept instead of dropped, shown as worth nothing
/// until it is known, and moved into the run it was copied on — once, however often the repricing runs — as soon as the
/// SDE knows it. Headless, against a temp store.
/// </summary>
public sealed class UnrecognisedLootTests
{
    private const string NewItem = "Crimson Harvest Token";
    private const int NewItemTypeId = 99001;
    private const int Tritanium = 34;
    private const long CharacterId = 90000001;
    private const string ServerAddress = "https://fleet.example";
    private static readonly DateTime StartedAtUtc = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task APasteWithAnUnknownName_KeepsItAsOpen_AndTheRunTotalCountsItAsZero()
    {
        using TestClientInstance instance = (await _InstanceAsync()).Instance;
        Guid runId = await _SaveRunAsync(instance, (NewItem, 3));

        UnrecognisedLootLine line = Assert.Single(await _LinesAsync(instance));
        Assert.Equal((NewItem, 3L, UnrecognisedItemStatus.Open, CharacterId), (line.Name, line.Quantity, line.Status, line.CharacterId));
        Assert.Equal(UnrecognisedItemSource.RunWindowEntry, line.Source);
        Assert.Equal(500m, (await _SummaryAsync(instance)).LootIskNet);

        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var overview = await dispatcher.Query(new GetRunLootQuery(runId), Token);
        UnrecognisedLootLineDto shown = Assert.Single(Assert.Single(overview.Value!.Captures).UnrecognisedLines!);
        Assert.Equal((NewItem, 3L), (shown.Name, shown.Quantity));
    }

    [AvaloniaFact]
    public async Task TheLootSection_ListsTheUnrecognisedName_SaysItCountsAsZero_AndKeepsItWhenTheListIsRewritten()
    {
        (TestClientInstance instance, FakeSdeAccessor sde) = await _InstanceAsync();
        using var disposeInstance = instance;
        Guid runId = await _SaveRunAsync(instance, (NewItem, 3));
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var activity = new ActivityLootViewModel(() => new RunLootViewModel(dispatcher,
            instance.Services.GetRequiredService<IAppraisalProvider>(), sde));

        ActivityLootCharacterViewModel block = activity.Show(runId, CharacterId, "Pilot");
        await block.Loot.RefreshAsync(Token);

        UnrecognisedLootRowViewModel row = Assert.Single(block.Loot.UnrecognisedRows);
        Assert.Equal($"{NewItem} ×3", row.NameWithQuantityText);
        Assert.Equal("unrecognised · counts as 0", row.StatusText);
        Assert.Equal("1 copied name is not recognised yet and counts as 0 until the EVE static data knows it.", activity.UnrecognisedText);
        block.Loot.BeginLootEditCommand.Execute(null);
        Assert.Contains($"{NewItem}\t3", block.Loot.LootEditor.Text);
    }

    [AvaloniaFact]
    public async Task ANewSde_MovesTheNameIntoItsCapture_PricesTheRun_AndMarksAPublishedRunChangedSincePublished()
    {
        (TestClientInstance instance, FakeSdeAccessor sde) = await _InstanceAsync();
        using var disposeInstance = instance;
        Guid runId = await _SaveRunAsync(instance, (NewItem, 3));
        await _MarkPublishedAsync(instance, runId);
        int revisionBefore = (await _RunAsync(instance, runId)).Revision;
        sde.Add(NewItemTypeId, NewItem, 18, 4);
        await _PriceAsync(instance, (Tritanium, 5), (NewItemTypeId, 1000));
        IEventBus bus = instance.Services.GetRequiredService<IEventBus>();
        List<Guid?> runsChanged = [];
        List<Guid> lootCorrected = [];
        using IDisposable runsListening = bus.Subscribe<RunsChangedEvent>(changed => runsChanged.Add(changed.Data.RunId));
        using IDisposable lootListening = bus.Subscribe<RunLootCorrectedEvent>(corrected => lootCorrected.Add(corrected.Data));

        Result<int> moved = await instance.Services.GetRequiredService<IDispatcher>().Send(new RepriceUnrecognisedLootCommand(), Token);

        Assert.Equal(1, moved.Value);
        Assert.Contains(runId, runsChanged.OfType<Guid>());
        Assert.Equal([runId], lootCorrected);
        UnrecognisedLootLine line = Assert.Single(await _LinesAsync(instance));
        Assert.Equal(UnrecognisedItemStatus.Resolved, line.Status);
        Assert.Equal(NewItemTypeId, line.ResolvedTypeId);
        Assert.Equal(1000m, line.ResolvedUnitPrice);
        Assert.NotNull(line.ResolvedAtUtc);
        RunLootEntry entry = Assert.Single(await _EntriesAsync(instance), candidate => candidate.ItemTypeId == NewItemTypeId);
        Assert.Equal(line.ResolvedRunLootEntryId, entry.Id);
        Assert.Equal((NewItem, 3L, LootKind.Gained), (entry.Name, entry.Quantity, entry.LootKind));
        Assert.Equal(500m + 3000m, (await _SummaryAsync(instance)).LootIskNet);
        Run run = await _RunAsync(instance, runId);
        Assert.Equal(RunSyncState.Outdated, run.SyncState);
        Assert.Equal(revisionBefore + 1, run.Revision);
    }

    [AvaloniaFact]
    public async Task Repricing_IsIdempotent_AndTwoPassesAtOnceNeverCountTheNameTwice()
    {
        // The pass reads its open lines, then asks for prices, then writes: slowed there, a second pass that did not wait
        // would have read the same open line and added its entry too.
        (TestClientInstance instance, FakeSdeAccessor sde) = await _InstanceAsync(services =>
            services.Decorate<IMarketPriceRepository>((inner, _) => new SlowPriceReads(inner)));
        using var disposeInstance = instance;
        Guid runId = await _SaveRunAsync(instance, (NewItem, 3));
        sde.Add(NewItemTypeId, NewItem, 18, 4);
        await _PriceAsync(instance, (Tritanium, 5), (NewItemTypeId, 1000));
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();

        Result<int>[] concurrent = await Task.WhenAll(
            dispatcher.Send(new RepriceUnrecognisedLootCommand(), Token),
            dispatcher.Send(new RepriceUnrecognisedLootCommand(), Token));
        Result<int> again = await dispatcher.Send(new RepriceUnrecognisedLootCommand(), Token);

        Assert.Equal(1, concurrent.Sum(result => result.Value));
        Assert.Equal(0, again.Value);
        Assert.Single(await _EntriesAsync(instance), entry => entry.ItemTypeId == NewItemTypeId);
        int revision = (await _RunAsync(instance, runId)).Revision;
        await dispatcher.Send(new RepriceUnrecognisedLootCommand(), Token);
        Assert.Equal(revision, (await _RunAsync(instance, runId)).Revision);
    }

    [AvaloniaFact]
    public async Task ARunWithOnlyKnownItems_IsLeftAloneByRepricing()
    {
        using TestClientInstance instance = (await _InstanceAsync()).Instance;
        Guid runId = await _SaveRunAsync(instance);
        Run before = await _RunAsync(instance, runId);

        Result<int> moved = await instance.Services.GetRequiredService<IDispatcher>().Send(new RepriceUnrecognisedLootCommand(), Token);

        Assert.Equal(0, moved.Value);
        Run after = await _RunAsync(instance, runId);
        Assert.Equal((before.Revision, before.SyncState), (after.Revision, after.SyncState));
        Assert.Equal(500m, (await _SummaryAsync(instance)).LootIskNet);
    }

    [AvaloniaFact]
    public async Task ANameTheSdeStillDoesNotKnow_StaysOpen_OnRepricing()
    {
        using TestClientInstance instance = (await _InstanceAsync()).Instance;
        await _SaveRunAsync(instance, (NewItem, 3));

        Result<int> moved = await instance.Services.GetRequiredService<IDispatcher>().Send(new RepriceUnrecognisedLootCommand(), Token);

        Assert.Equal(0, moved.Value);
        Assert.Equal(UnrecognisedItemStatus.Open, Assert.Single(await _LinesAsync(instance)).Status);
    }

    [AvaloniaFact]
    public async Task TheLog_ShowsOpenNamesByDefault_AddsThemUpOverRuns_AndKeepsResolvedOnesBehindAFilter()
    {
        (TestClientInstance instance, FakeSdeAccessor sde) = await _InstanceAsync();
        using var disposeInstance = instance;
        await _SaveRunAsync(instance, (NewItem, 3), ("Crimson Harvest Mask", 1));
        await _SaveRunAsync(instance, new RunSpec { CharacterId = 90000002, Unrecognised = [(NewItem, 4)] });
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();

        IReadOnlyList<UnrecognisedLootItemDto> open = (await dispatcher.Query(new GetUnrecognisedLootQuery(), Token)).Value!;

        UnrecognisedLootItemDto token = Assert.Single(open, item => item.Name == NewItem);
        Assert.Equal((7L, 2), (token.TotalQuantity, token.RunCount));
        Assert.Equal(2, open.Count);

        sde.Add(NewItemTypeId, NewItem, 18, 4);
        await dispatcher.Send(new RepriceUnrecognisedLootCommand(), Token);

        Assert.Equal("Crimson Harvest Mask", Assert.Single((await dispatcher.Query(new GetUnrecognisedLootQuery(), Token)).Value!).Name);
        UnrecognisedLootItemDto resolved = Assert.Single(
            (await dispatcher.Query(new GetUnrecognisedLootQuery(UnrecognisedItemStatus.Resolved), Token)).Value!);
        Assert.Equal((NewItem, NewItemTypeId), (resolved.Name, resolved.ResolvedTypeId));
    }

    [AvaloniaFact]
    public async Task AnAppraisedUnknownName_IsLoggedOnce_NoMatterHowOftenItIsAppraised()
    {
        using TestClientInstance instance = (await _InstanceAsync()).Instance;
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        var names = new[] { new UnrecognisedLootNameInput { Name = NewItem, Quantity = 5 } };

        Result<int> first = await dispatcher.Send(new RecordUnrecognisedItemsCommand(names), Token);
        Result<int> second = await dispatcher.Send(new RecordUnrecognisedItemsCommand(names), Token);

        Assert.Equal((1, 0), (first.Value, second.Value));
        UnrecognisedLootLine line = Assert.Single(await _LinesAsync(instance));
        Assert.Equal((UnrecognisedItemSource.Appraisal, (Guid?)null), (line.Source, line.RunLootCaptureId));
    }

    [AvaloniaFact]
    public async Task AnUntypedMissionReward_ShowsInTheLog_AndGetsItsTypeOnceTheSdeKnowsIt_MarkingThePublishedRun()
    {
        (TestClientInstance instance, FakeSdeAccessor sde) = await _InstanceAsync();
        using var disposeInstance = instance;
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Guid runId = await _SaveRunAsync(instance, new RunSpec
        {
            Parameters =
            [
                new RunParameterInput
                {
                    ParameterKey = RunParameterKey.Item,
                    TypedValue = $"2 x {NewItem}",
                    Amount = 2,
                    ObservedAtUtc = StartedAtUtc
                }
            ]
        });
        await _MarkPublishedAsync(instance, runId);
        int revisionBefore = (await _RunAsync(instance, runId)).Revision;

        UnrecognisedLootItemDto open = Assert.Single((await dispatcher.Query(new GetUnrecognisedLootQuery(), Token)).Value!);
        Assert.Equal((NewItem, 2L, UnrecognisedItemSource.MissionReward), (open.Name, open.TotalQuantity, Assert.Single(open.Sources)));

        sde.Add(NewItemTypeId, NewItem, 18, 4);
        Result<int> moved = await dispatcher.Send(new RepriceUnrecognisedLootCommand(), Token);
        Result<int> again = await dispatcher.Send(new RepriceUnrecognisedLootCommand(), Token);

        Assert.Equal((1, 0), (moved.Value, again.Value));
        RunParameter parameter = Assert.Single(await _ParametersAsync(instance, runId));
        Assert.Equal(NewItemTypeId, parameter.ItemTypeId);
        UnrecognisedLootLine history = Assert.Single(await _LinesAsync(instance));
        Assert.Equal((UnrecognisedItemStatus.Resolved, UnrecognisedItemSource.MissionReward, parameter.Id),
            (history.Status, history.Source, history.ResolvedRunParameterId));
        Run run = await _RunAsync(instance, runId);
        Assert.Equal((RunSyncState.Outdated, revisionBefore + 1), (run.SyncState, run.Revision));
        Assert.Empty((await dispatcher.Query(new GetUnrecognisedLootQuery(), Token)).Value!);
    }

    [AvaloniaFact]
    public async Task APriceRefresh_AddsUpAgainOnlyTheActivitiesStillHoldingUnpricedLoot()
    {
        (TestClientInstance instance, FakeSdeAccessor sde) = await _InstanceAsync();
        using var disposeInstance = instance;
        sde.Add(NewItemTypeId, NewItem, 18, 4);
        Guid priced = await _SaveRunAsync(instance, new RunSpec { CharacterId = 90000002 });
        Guid unpriced = await _SaveRunAsync(instance, new RunSpec { ExtraKnown = (NewItemTypeId, NewItem, 2) });
        Assert.Equal(500m, (await _SummaryOfAsync(instance, unpriced)).LootIskNet);
        DateTime pricedBuiltAt = (await _SummaryOfAsync(instance, priced)).ComputedAtUtc;

        await _PriceAsync(instance, (Tritanium, 5), (NewItemTypeId, 1000));
        await instance.Services.GetRequiredService<UnrecognisedLootRepricer>().RepriceAsync(Token);

        Assert.Equal(500m + 2000m, (await _SummaryOfAsync(instance, unpriced)).LootIskNet);
        Assert.Equal(pricedBuiltAt, (await _SummaryOfAsync(instance, priced)).ComputedAtUtc);
    }

    [AvaloniaFact]
    public async Task TheUnrecognisedLines_TravelOnTheWire_AndAPayloadFromAnOlderClientReadsAsNone()
    {
        using TestClientInstance instance = (await _InstanceAsync()).Instance;
        Guid runId = await _SaveRunAsync(instance, (NewItem, 3));
        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);
        Run run = await db.Set<Run>().AsNoTracking()
            .Include(candidate => candidate.LootCaptures).ThenInclude(capture => capture.Entries)
            .Include(candidate => candidate.LootCaptures).ThenInclude(capture => capture.UnrecognisedLines)
            .SingleAsync(candidate => candidate.Id == runId, Token);

        string json = JsonSerializer.Serialize(RunWireData.FromEntity(run));
        Run received = JsonSerializer.Deserialize<RunWireData>(json)!.ToEntity();

        UnrecognisedLootLine carried = Assert.Single(Assert.Single(received.LootCaptures).UnrecognisedLines);
        Assert.Equal((NewItem, 3L, UnrecognisedItemStatus.Open), (carried.Name, carried.Quantity, carried.Status));

        using JsonDocument document = JsonDocument.Parse(json);
        var older = JsonSerializer.Serialize(document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Name == nameof(RunWireData.LootCaptures)
                ? (object)property.Value.EnumerateArray().Select(capture => capture.EnumerateObject()
                    .Where(field => field.Name != nameof(RunLootCaptureWireData.UnrecognisedLines))
                    .ToDictionary(field => field.Name, field => (object)field.Value)).ToList()
                : property.Value));
        Run fromOlder = JsonSerializer.Deserialize<RunWireData>(older)!.ToEntity();
        Assert.Empty(Assert.Single(fromOlder.LootCaptures).UnrecognisedLines);
    }

    private static async Task<(TestClientInstance Instance, FakeSdeAccessor Sde)> _InstanceAsync(Action<IServiceCollection>? configure = null)
    {
        var sde = new FakeSdeAccessor().Add(Tritanium, "Tritanium", 18, 4);
        TestClientInstance instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<ISdeAccessor>(sde);
            configure?.Invoke(services);
        });
        await _PriceAsync(instance, (Tritanium, 5));
        return (instance, sde);
    }

    private sealed class SlowPriceReads(IMarketPriceRepository inner) : IMarketPriceRepository
    {
        public Task ReplaceAllAsync(IReadOnlyCollection<LocalMarketPrice> prices, CancellationToken cancellationToken = default) =>
            inner.ReplaceAllAsync(prices, cancellationToken);

        public async Task<IReadOnlyDictionary<int, double>> GetAveragePricesAsync(
            IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken = default)
        {
            await Task.Delay(300, cancellationToken);
            return await inner.GetAveragePricesAsync(typeIds, cancellationToken);
        }

        public Task<IReadOnlyDictionary<int, double>> GetAdjustedPricesAsync(
            IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken = default) =>
            inner.GetAdjustedPricesAsync(typeIds, cancellationToken);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) => inner.CountAsync(cancellationToken);

        public Task<DateTimeOffset?> GetSnapshotTimeAsync(CancellationToken cancellationToken = default) =>
            inner.GetSnapshotTimeAsync(cancellationToken);
    }

    private static Task _PriceAsync(TestClientInstance instance, params (int TypeId, double Price)[] prices) =>
        instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [.. prices.Select(price => new LocalMarketPrice
            {
                TypeId = price.TypeId, AveragePrice = price.Price, AdjustedPrice = price.Price, UpdatedAt = DateTimeOffset.UtcNow
            })], Token);

    private static Task<Guid> _SaveRunAsync(TestClientInstance instance, params (string Name, long Quantity)[] unrecognised) =>
        _SaveRunAsync(instance, new RunSpec { Unrecognised = unrecognised });

    private sealed class RunSpec
    {
        public long CharacterId { get; init; } = UnrecognisedLootTests.CharacterId;
        public (string Name, long Quantity)[] Unrecognised { get; init; } = [];
        public IReadOnlyList<RunParameterInput> Parameters { get; init; } = [];
        public (int TypeId, string Name, long Quantity)? ExtraKnown { get; init; }
    }

    private static async Task<Guid> _SaveRunAsync(TestClientInstance instance, RunSpec spec)
    {
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(spec.CharacterId, ActivityKind.Site, StartedAtUtc,
            1234, "Blood Refuge", 30000142), Token);
        Assert.True(started.IsSuccess);
        List<RunLootEntryInput> known =
        [
            new RunLootEntryInput { ItemTypeId = Tritanium, Name = "Tritanium", Quantity = 100, LootKind = LootKind.Gained }
        ];
        if (spec.ExtraKnown is { } extra)
            known.Add(new RunLootEntryInput { ItemTypeId = extra.TypeId, Name = extra.Name, Quantity = extra.Quantity, LootKind = LootKind.Gained });
        Result saved = await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
        [
            new RunLootCaptureInput
            {
                CapturedAtUtc = StartedAtUtc.AddMinutes(10),
                Source = LootCaptureSource.Pasted,
                CharacterId = spec.CharacterId,
                Entries = known,
                UnrecognisedNames = [.. spec.Unrecognised.Select(name => new UnrecognisedLootNameInput { Name = name.Name, Quantity = name.Quantity })]
            }
        ], [], [], spec.Parameters), Token);
        Assert.True(saved.IsSuccess);
        await _RebuildAsync(instance);
        return started.Value;
    }

    private static async Task _RebuildAsync(TestClientInstance instance) =>
        Assert.True((await instance.Services.GetRequiredService<IDispatcher>().Send(new RebuildActivitySummariesCommand(), Token)).IsSuccess);

    private static async Task<ActivitySummary> _SummaryAsync(TestClientInstance instance)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        return Assert.Single(await db.Set<ActivitySummary>().AsNoTracking().ToListAsync(Token));
    }

    private static async Task<ActivitySummary> _SummaryOfAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        return await db.Set<ActivitySummary>().AsNoTracking().SingleAsync(summary => summary.RunId == runId, Token);
    }

    private static async Task<List<UnrecognisedLootLine>> _LinesAsync(TestClientInstance instance)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        return await db.Set<UnrecognisedLootLine>().AsNoTracking().ToListAsync(Token);
    }

    private static async Task<List<RunLootEntry>> _EntriesAsync(TestClientInstance instance)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        return await db.Set<RunLootEntry>().AsNoTracking().ToListAsync(Token);
    }

    private static async Task<List<RunParameter>> _ParametersAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        return await db.Set<RunParameter>().AsNoTracking().Where(parameter => parameter.RunId == runId).ToListAsync(Token);
    }

    private static async Task<Run> _RunAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        return await db.Set<Run>().AsNoTracking().SingleAsync(run => run.Id == runId, Token);
    }

    private static async Task _MarkPublishedAsync(TestClientInstance instance, Guid runId)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        await db.Set<Run>().Where(run => run.Id == runId).ExecuteUpdateAsync(properties => properties
            .SetProperty(run => run.SyncState, RunSyncState.Synced)
            .SetProperty(run => run.SyncServerAddress, ServerAddress)
            .SetProperty(run => run.LastPushedAtUtc, DateTime.UtcNow), Token);
    }

    private static Task<ClientDbContext> _DbAsync(TestClientInstance instance) =>
        instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);
}
