using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Commands;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Killmails.Enums;
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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-463: a run is worth what its loot, ore and filament were worth when they came in. The price is fixed on each line
/// as it is stored, a line without one takes the first price there is once, and only "Re-value at current prices"
/// moves it after that. A loss linked to the run follows the same rule from the moment it is linked (ET-464). Headless,
/// against a temp store.
/// </summary>
public sealed class RunPriceSnapshotTests
{
    private const int Tritanium = 34;
    private const int Pyerite = 35;
    private const int Veldspar = 1230;
    private const int Filament = 60000;
    private const int Rifter = 587;
    private const int Hobgoblin = 2488;
    private const long Pilot = 90000001;
    private const long Fleetmate = 90000002;
    private const string ServerAddress = "https://fleet.example";
    private static readonly DateTime StartedAtUtc = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task SavedRun_KeepsItsLootTotal_WhenTheCachePriceMoves()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, (Tritanium, 5));
        Guid runId = await _SaveRunAsync(instance, Pilot);
        int revision = (await _RunAsync(instance, runId)).Revision;
        await _PriceAsync(instance, (Tritanium, 50));

        await _RebuildAsync(instance);
        await _Dispatcher(instance).Send(new FillRunPriceSnapshotsCommand(), Token);

        RunLootEntry entry = Assert.Single(await _EntriesAsync(instance));
        Assert.Equal((5m, PriceSnapshotSource.Capture), (entry.UnitPriceIsk, entry.PriceSource));
        Assert.NotNull(entry.PricedAtUtc);
        Assert.Equal(500m, (await _SummaryAsync(instance)).LootIskNet);
        Assert.Equal(revision, (await _RunAsync(instance, runId)).Revision);
    }

    [AvaloniaFact]
    public async Task OpeningTheDetail_DoesNotAddUpAgain_WhenEveryLineHasAFixedPrice()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, (Tritanium, 5));
        Guid runId = await _SaveRunAsync(instance, Pilot);
        await _PriceAsync(instance, (Tritanium, 50));

        Result<int> rebuilt = await _Dispatcher(instance).Send(
            new RebuildActivitySummariesCommand(runId, OnlyWhenPricesChanged: true), Token);

        Assert.Equal(0, rebuilt.Value);
    }

    [AvaloniaFact]
    public async Task OpeningTheDetail_AddsUpAgain_WhileALineIsStillValuedLive()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, (Tritanium, 5));
        Guid runId = await _SaveRunAsync(instance, Fleetmate, (Pyerite, 10));
        await _PriceAsync(instance, (Tritanium, 50), (Pyerite, 7));

        Result<int> rebuilt = await _Dispatcher(instance).Send(
            new RebuildActivitySummariesCommand(runId, OnlyWhenPricesChanged: true), Token);

        Assert.Equal(1, rebuilt.Value);
        Assert.Equal(500m + 70m, (await _SummaryAsync(instance)).LootIskNet);
    }

    [AvaloniaFact]
    public async Task Fill_GivesAnUnpricedLineTheFirstPriceOnce_AndMarksAPublishedRunChangedSincePublished()
    {
        using TestClientInstance instance = _Instance();
        await _AddLocalCharacterAsync(instance, Pilot);
        await _PriceAsync(instance, (Tritanium, 5));
        Guid runId = await _SaveRunAsync(instance, Pilot, (Pyerite, 10));
        await _MarkPublishedAsync(instance, runId);
        int revisionBefore = (await _RunAsync(instance, runId)).Revision;
        await _PriceAsync(instance, (Tritanium, 50), (Pyerite, 7));
        IEventBus bus = instance.Services.GetRequiredService<IEventBus>();
        List<Guid?> runsChanged = [];
        List<Guid> lootCorrected = [];
        using IDisposable runsListening = bus.Subscribe<RunsChangedEvent>(changed => runsChanged.Add(changed.Data.RunId));
        using IDisposable lootListening = bus.Subscribe<RunLootCorrectedEvent>(corrected => lootCorrected.Add(corrected.Data));

        Result<int> filled = await _Dispatcher(instance).Send(new FillRunPriceSnapshotsCommand(), Token);

        Assert.Equal(1, filled.Value);
        Assert.Contains(runId, runsChanged.OfType<Guid>());
        Assert.Equal([runId], lootCorrected);
        RunLootEntry pyerite = Assert.Single(await _EntriesAsync(instance), entry => entry.ItemTypeId == Pyerite);
        Assert.Equal((7m, PriceSnapshotSource.Backfill), (pyerite.UnitPriceIsk, pyerite.PriceSource));
        RunLootEntry tritanium = Assert.Single(await _EntriesAsync(instance), entry => entry.ItemTypeId == Tritanium);
        Assert.Equal((5m, PriceSnapshotSource.Capture), (tritanium.UnitPriceIsk, tritanium.PriceSource));
        Assert.Equal(500m + 70m, (await _SummaryAsync(instance)).LootIskNet);
        Run run = await _RunAsync(instance, runId);
        Assert.Equal((RunSyncState.Outdated, revisionBefore + 1), (run.SyncState, run.Revision));
    }

    [AvaloniaFact]
    public async Task Fill_NeverMovesALineItAlreadyPriced_WhenThePriceMovesAgain()
    {
        using TestClientInstance instance = _Instance();
        await _AddLocalCharacterAsync(instance, Pilot);
        await _PriceAsync(instance, (Tritanium, 5));
        Guid runId = await _SaveRunAsync(instance, Pilot, (Pyerite, 10));
        await _PriceAsync(instance, (Tritanium, 5), (Pyerite, 7));
        IDispatcher dispatcher = _Dispatcher(instance);
        Assert.Equal(1, (await dispatcher.Send(new FillRunPriceSnapshotsCommand(), Token)).Value);
        int revision = (await _RunAsync(instance, runId)).Revision;
        await _PriceAsync(instance, (Tritanium, 5), (Pyerite, 700));

        Result<int> again = await dispatcher.Send(new FillRunPriceSnapshotsCommand(), Token);
        await _RebuildAsync(instance);

        Assert.Equal(0, again.Value);
        Assert.Equal(7m, Assert.Single(await _EntriesAsync(instance), entry => entry.ItemTypeId == Pyerite).UnitPriceIsk);
        Assert.Equal(500m + 70m, (await _SummaryAsync(instance)).LootIskNet);
        Assert.Equal(revision, (await _RunAsync(instance, runId)).Revision);
    }

    [AvaloniaFact]
    public async Task Fill_LeavesAFleetmatesRunAlone_AndItStaysValuedLive()
    {
        using TestClientInstance instance = _Instance();
        await _AddLocalCharacterAsync(instance, Pilot);
        await _PriceAsync(instance, (Tritanium, 5));
        Guid runId = await _SaveRunAsync(instance, Fleetmate, (Pyerite, 10));
        int revision = (await _RunAsync(instance, runId)).Revision;
        await _PriceAsync(instance, (Tritanium, 5), (Pyerite, 7));

        Result<int> filled = await _Dispatcher(instance).Send(new FillRunPriceSnapshotsCommand(), Token);
        await _RebuildAsync(instance);

        Assert.Equal(0, filled.Value);
        Assert.Null(Assert.Single(await _EntriesAsync(instance), entry => entry.ItemTypeId == Pyerite).UnitPriceIsk);
        Assert.Equal(500m + 70m, (await _SummaryAsync(instance)).LootIskNet);
        Assert.Equal(revision, (await _RunAsync(instance, runId)).Revision);
    }

    [AvaloniaFact]
    public async Task Revalue_SetsEveryLineToTheCurrentPrice_AndMarksAPublishedRunChangedSincePublished()
    {
        using TestClientInstance instance = _Instance();
        await _AddLocalCharacterAsync(instance, Pilot);
        await _PriceAsync(instance, (Tritanium, 5));
        Guid runId = await _SaveRunAsync(instance, Pilot);
        await _MarkPublishedAsync(instance, runId);
        int revisionBefore = (await _RunAsync(instance, runId)).Revision;
        await _PriceAsync(instance, (Tritanium, 8));
        IEventBus bus = instance.Services.GetRequiredService<IEventBus>();
        List<Guid?> runsChanged = [];
        List<Guid> lootCorrected = [];
        using IDisposable runsListening = bus.Subscribe<RunsChangedEvent>(changed => runsChanged.Add(changed.Data.RunId));
        using IDisposable lootListening = bus.Subscribe<RunLootCorrectedEvent>(corrected => lootCorrected.Add(corrected.Data));

        Result<int> revalued = await _Dispatcher(instance).Send(new RevalueRunsCommand([runId]), Token);

        Assert.Equal(1, revalued.Value);
        Assert.Contains(runId, runsChanged.OfType<Guid>());
        Assert.Equal([runId], lootCorrected);
        RunLootEntry entry = Assert.Single(await _EntriesAsync(instance));
        Assert.Equal((8m, PriceSnapshotSource.Revalued), (entry.UnitPriceIsk, entry.PriceSource));
        Assert.Equal(800m, (await _SummaryAsync(instance)).LootIskNet);
        Run run = await _RunAsync(instance, runId);
        Assert.Equal((RunSyncState.Outdated, revisionBefore + 1), (run.SyncState, run.Revision));
    }

    [AvaloniaFact]
    public async Task Revalue_AtTheSamePrices_ChangesNothingAndSignalsNothing()
    {
        using TestClientInstance instance = _Instance();
        await _AddLocalCharacterAsync(instance, Pilot);
        await _PriceAsync(instance, (Tritanium, 5));
        Guid runId = await _SaveRunAsync(instance, Pilot);
        int revision = (await _RunAsync(instance, runId)).Revision;
        List<RunsChangedEventData> runsChanged = [];
        using IDisposable listening = instance.Services.GetRequiredService<IEventBus>()
            .Subscribe<RunsChangedEvent>(changed => runsChanged.Add(changed.Data));

        Result<int> revalued = await _Dispatcher(instance).Send(new RevalueRunsCommand([runId]), Token);

        Assert.Equal(0, revalued.Value);
        Assert.Empty(runsChanged);
        Assert.Equal(revision, (await _RunAsync(instance, runId)).Revision);
    }

    [AvaloniaFact]
    public async Task Revalue_RefusesAFleetmatesRun_AndAnUnknownOne()
    {
        using TestClientInstance instance = _Instance();
        await _AddLocalCharacterAsync(instance, Pilot);
        await _PriceAsync(instance, (Tritanium, 5));
        Guid foreign = await _SaveRunAsync(instance, Fleetmate);
        await _PriceAsync(instance, (Tritanium, 8));
        IDispatcher dispatcher = _Dispatcher(instance);

        Result<int> theirs = await dispatcher.Send(new RevalueRunsCommand([foreign]), Token);
        Result<int> unknown = await dispatcher.Send(new RevalueRunsCommand([Guid.CreateVersion7()]), Token);
        Result<int> none = await dispatcher.Send(new RevalueRunsCommand([]), Token);

        Assert.Equal(MessageCodes.ValidationFailed, Assert.Single(theirs.Messages).Code);
        Assert.Equal(MessageCodes.NotFound, Assert.Single(unknown.Messages).Code);
        Assert.Equal(MessageCodes.ValidationFailed, Assert.Single(none.Messages).Code);
        Assert.Equal(5m, Assert.Single(await _EntriesAsync(instance)).UnitPriceIsk);
    }

    [AvaloniaFact]
    public async Task Mining_IsPricedAtItsFirstCycle_AndKeepsThatPriceOverLaterCyclesAndRefreshes()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, (Veldspar, 20));
        IDispatcher dispatcher = _Dispatcher(instance);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Site, StartedAtUtc, 1234,
            "Ore Site", 30000142), Token);
        await dispatcher.Send(new AddRunMiningEntryCommand(Pilot, StartedAtUtc.AddMinutes(1), "Veldspar", 100, false, 0), Token);
        await _PriceAsync(instance, (Veldspar, 90));
        await dispatcher.Send(new AddRunMiningEntryCommand(Pilot, StartedAtUtc.AddMinutes(2), "Veldspar", 50, false, 0), Token);
        Assert.True((await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], []), Token)).IsSuccess);

        await _RebuildAsync(instance);

        await using ClientDbContext db = await _DbAsync(instance);
        RunMiningEntry entry = await db.Set<RunMiningEntry>().AsNoTracking().SingleAsync(Token);
        Assert.Equal((150, 20m, PriceSnapshotSource.Capture), (entry.Units, entry.UnitPriceIsk, entry.PriceSource));
        Assert.Equal(150 * 20m, (await _SummaryAsync(instance)).TotalIsk);
    }

    [AvaloniaFact]
    public async Task Filament_IsPricedAtSave_AndItsCostStaysWhenThePriceMoves()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, (Tritanium, 5), (Filament, 1000));
        IDispatcher dispatcher = _Dispatcher(instance);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Abyssal, StartedAtUtc, 0, null, null), Token);
        Assert.True((await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(20), StartedAtUtc.AddMinutes(21),
            [], [], [],
            [
                new RunParameterInput { ParameterKey = RunParameterKey.AbyssalFilamentTypeId, TypedValue = "60000", ObservedAtUtc = StartedAtUtc },
                new RunParameterInput { ParameterKey = RunParameterKey.AbyssalFilamentCount, TypedValue = "2", ObservedAtUtc = StartedAtUtc }
            ]), Token)).IsSuccess);
        await _PriceAsync(instance, (Tritanium, 5), (Filament, 9000));

        await _RebuildAsync(instance);

        await using ClientDbContext db = await _DbAsync(instance);
        RunParameter typeRow = await db.Set<RunParameter>().AsNoTracking()
            .SingleAsync(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilamentTypeId, Token);
        Assert.Equal((1000m, PriceSnapshotSource.Capture), (typeRow.UnitPriceIsk, typeRow.PriceSource));
        Assert.Null((await db.Set<RunParameter>().AsNoTracking()
            .SingleAsync(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilamentCount, Token)).UnitPriceIsk);
        Assert.Equal(-2000m, (await _SummaryAsync(instance)).TotalIsk);
    }

    [AvaloniaFact]
    public async Task HandWrittenList_KeepsThePriceTheRunAlreadyFixedForAType()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, (Tritanium, 5));
        Guid runId = await _SaveRunAsync(instance, Pilot);
        await _PriceAsync(instance, (Tritanium, 50), (Pyerite, 7));

        Result<Guid> written = await _Dispatcher(instance).Send(new SetRunLootManualCommand(runId, StartedAtUtc.AddHours(1),
        [
            new RunLootEntryInput { ItemTypeId = Tritanium, Name = "Tritanium", Quantity = 200, LootKind = LootKind.Gained },
            new RunLootEntryInput { ItemTypeId = Pyerite, Name = "Pyerite", Quantity = 10, LootKind = LootKind.Gained }
        ], []), Token);

        Assert.True(written.IsSuccess);
        Assert.Equal(200 * 5m + 10 * 7m, (await _SummaryAsync(instance)).LootIskNet);
    }

    [AvaloniaFact]
    public async Task TheLootTable_ShowsTheFixedPrice_AndMarksOnlyALineWithoutOneAsLive()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, (Tritanium, 5));
        Guid runId = await _SaveRunAsync(instance, Fleetmate, (Pyerite, 10));
        await _PriceAsync(instance, (Tritanium, 50), (Pyerite, 7));
        var loot = new RunLootViewModel(_Dispatcher(instance), instance.Services.GetRequiredService<IAppraisalProvider>()) { RunId = runId };

        await loot.RefreshAsync(Token);

        ActivityLootLineViewModel tritanium = Assert.Single(loot.ItemRows, row => row.ItemTypeId == Tritanium);
        ActivityLootLineViewModel pyerite = Assert.Single(loot.ItemRows, row => row.ItemTypeId == Pyerite);
        Assert.Equal(("500", false), (tritanium.AmountText, tritanium.IsLivePrice));
        Assert.Equal(("70 · live", true), (pyerite.AmountText, pyerite.IsLivePrice));
        Assert.Equal(570m, loot.LootIsk);
    }

    [AvaloniaFact]
    public async Task TheLootTable_SaysTheFiguresAreTheFixedOnes_WhenNoLineIsLive()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, (Tritanium, 5));
        Guid runId = await _SaveRunAsync(instance, Pilot);
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync([], Token);
        var loot = new RunLootViewModel(_Dispatcher(instance), instance.Services.GetRequiredService<IAppraisalProvider>()) { RunId = runId };

        await loot.RefreshAsync(Token);

        Assert.Equal(500m, loot.LootIsk);
        Assert.Null(loot.PricingProblemText);
        Assert.Equal("Valued at the price each item had when it came in.", loot.TotalIskLabel);
    }

    [Fact]
    public void WireData_CarriesTheFixedPrices_BothWays()
    {
        DateTime pricedAtUtc = StartedAtUtc.AddMinutes(3);
        var run = new Run { Id = Guid.CreateVersion7(), CharacterId = Pilot, StartedAtUtc = StartedAtUtc, State = RunState.Saved };
        var capture = new RunLootCapture { Id = Guid.CreateVersion7(), RunId = run.Id, CapturedAtUtc = StartedAtUtc };
        capture.Entries.Add(new RunLootEntry
        {
            Id = Guid.CreateVersion7(), ItemTypeId = Tritanium, Name = "Tritanium", Quantity = 1, LootKind = LootKind.Gained,
            UnitPriceIsk = 5.25m, PricedAtUtc = pricedAtUtc, PriceSource = PriceSnapshotSource.Backfill
        });
        run.LootCaptures.Add(capture);
        run.MiningEntries.Add(new RunMiningEntry
        {
            Id = Guid.CreateVersion7(), RunId = run.Id, OreType = "Veldspar", Units = 10,
            UnitPriceIsk = 20m, PricedAtUtc = pricedAtUtc, PriceSource = PriceSnapshotSource.Capture
        });
        run.Parameters.Add(new RunParameter
        {
            Id = Guid.CreateVersion7(), RunId = run.Id, ParameterKey = RunParameterKey.AbyssalFilamentTypeId, TypedValue = "60000",
            UnitPriceIsk = 1000m, PricedAtUtc = pricedAtUtc, PriceSource = PriceSnapshotSource.Revalued
        });

        Run back = RunWireData.FromEntity(run).ToEntity();

        RunLootEntry entry = Assert.Single(Assert.Single(back.LootCaptures).Entries);
        Assert.Equal((5.25m, pricedAtUtc, PriceSnapshotSource.Backfill), (entry.UnitPriceIsk, entry.PricedAtUtc, entry.PriceSource));
        RunMiningEntry ore = Assert.Single(back.MiningEntries);
        Assert.Equal((20m, pricedAtUtc, PriceSnapshotSource.Capture), (ore.UnitPriceIsk, ore.PricedAtUtc, ore.PriceSource));
        RunParameter filament = Assert.Single(back.Parameters);
        Assert.Equal((1000m, pricedAtUtc, PriceSnapshotSource.Revalued), (filament.UnitPriceIsk, filament.PricedAtUtc, filament.PriceSource));
    }

    /// <summary>The migration over a store from before snapshots: loot and filament take the cache price of that day,
    /// marked Migrated; ores are marked and priced by the first fill, without counting as a correction. The list, the
    /// detail and the stored summary then agree, and keep agreeing after the market moves.</summary>
    [AvaloniaFact]
    public async Task Migration_FixesExistingLinesAtTheCachePrice_AndListDetailAndSummaryAgree()
    {
        using TestClientInstance instance = _Instance();
        await _AddLocalCharacterAsync(instance, Pilot);
        IDispatcher dispatcher = _Dispatcher(instance);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Abyssal, StartedAtUtc, 1234,
            "Ore Site", 30000142), Token);
        await dispatcher.Send(new AddRunMiningEntryCommand(Pilot, StartedAtUtc.AddMinutes(1), "Veldspar", 100, false, 0), Token);
        Assert.True((await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
        [
            new RunLootCaptureInput
            {
                CapturedAtUtc = StartedAtUtc.AddMinutes(10), Source = LootCaptureSource.Pasted, CharacterId = Pilot,
                Entries = [new RunLootEntryInput { ItemTypeId = Tritanium, Name = "Tritanium", Quantity = 100, LootKind = LootKind.Gained }]
            }
        ], [], [],
        [
            new RunParameterInput { ParameterKey = RunParameterKey.AbyssalFilamentTypeId, TypedValue = "60000", ObservedAtUtc = StartedAtUtc },
            new RunParameterInput { ParameterKey = RunParameterKey.AbyssalFilamentCount, TypedValue = "1", ObservedAtUtc = StartedAtUtc }
        ]), Token)).IsSuccess);
        await _MarkPublishedAsync(instance, started.Value);
        int revisionBefore = (await _RunAsync(instance, started.Value)).Revision;
        // ET-486: a fleet-shared killmail keeps its share columns through the round trip (the merged snapshots lost them).
        await using (ClientDbContext db = await _DbAsync(instance))
        {
            db.Set<LocalKillmail>().Add(new LocalKillmail { CharacterId = (int)Pilot, KillmailId = 1, Hash = "hash",
                KillmailTimeUtc = StartedAtUtc, SharedFromFleetId = 7, SharedFromServer = "local" });
            await db.SaveChangesAsync(Token);
        }
        await _MigrateAsync(instance, "20261006114905_AddUnrecognisedLootLine");
        await _PriceAsync(instance, (Tritanium, 5), (Veldspar, 20), (Filament, 300));

        await _MigrateAsync(instance, null);
        await dispatcher.Send(new FillRunPriceSnapshotsCommand(), Token);
        await _PriceAsync(instance, (Tritanium, 50), (Veldspar, 200), (Filament, 3000));
        await _RebuildAsync(instance);

        RunLootEntry loot = Assert.Single(await _EntriesAsync(instance));
        Assert.Equal((5m, PriceSnapshotSource.Migrated), (loot.UnitPriceIsk, loot.PriceSource));
        await using (ClientDbContext db = await _DbAsync(instance))
        {
            RunMiningEntry ore = await db.Set<RunMiningEntry>().AsNoTracking().SingleAsync(Token);
            Assert.Equal((20m, PriceSnapshotSource.Migrated), (ore.UnitPriceIsk, ore.PriceSource));
            RunParameter filament = await db.Set<RunParameter>().AsNoTracking()
                .SingleAsync(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilamentTypeId, Token);
            Assert.Equal((300m, PriceSnapshotSource.Migrated), (filament.UnitPriceIsk, filament.PriceSource));
            LocalKillmail shared = await db.Set<LocalKillmail>().AsNoTracking().SingleAsync(Token);
            Assert.Equal((7L, "local"), (shared.SharedFromFleetId, shared.SharedFromServer));
        }
        Run run = await _RunAsync(instance, started.Value);
        Assert.Equal((RunSyncState.Synced, revisionBefore), (run.SyncState, run.Revision));
        decimal expected = 100 * 5m + 100 * 20m - 300m;
        ActivitySummary summary = await _SummaryAsync(instance);
        Assert.Equal(expected, summary.TotalIsk);
        ActivityOverviewRowDto row = Assert.Single((await dispatcher.Query(new GetActivityOverviewQuery(), Token)).Value!);
        Assert.Equal(expected, row.Isk.Total);
        Assert.Equal(expected, (await dispatcher.Query(new GetActivityDetailQuery(summary.Id), Token)).Value!.Isk.Total);
    }

    /// <summary>ET-464 criteria 1 and 5. Red if the loss is still valued at the live price, by the summary or the detail,
    /// or if an activity whose loss is fully priced is still added up again on opening.</summary>
    [AvaloniaFact]
    public async Task LinkedLoss_KeepsTheValueItHadWhenItWasLinked_AndTheDetailNoLongerAddsUpAgain()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, (Tritanium, 5), (Rifter, 1_000), (Hobgoblin, 10));
        Guid runId = await _SaveRunAsync(instance, Pilot);
        await _AddLossAsync(instance, 1);

        await _Dispatcher(instance).Send(new SetKillmailRunLinkCommand((int)Pilot, 1, runId), Token);
        await _PriceAsync(instance, (Tritanium, 50), (Rifter, 9_000), (Hobgoblin, 90));
        Result<int> reopened = await _Dispatcher(instance).Send(
            new RebuildActivitySummariesCommand(runId, OnlyWhenPricesChanged: true), Token);
        await _RebuildAsync(instance);

        Assert.Equal(0, reopened.Value);
        decimal expected = 100 * 5m - (1_000m + 3 * 10m);
        ActivitySummary summary = await _SummaryAsync(instance);
        Assert.Equal(expected, summary.TotalIsk);
        Assert.Equal(expected, (await _Dispatcher(instance).Query(new GetActivityDetailQuery(summary.Id), Token)).Value?.Isk.Total);
        Assert.All(await _LossPricesAsync(instance), price => Assert.Equal(PriceSnapshotSource.Capture, price.PriceSource));
    }

    /// <summary>ET-464 criterion 1, the automatic link. Red if only a manual link fixes the prices.</summary>
    [AvaloniaFact]
    public async Task AutoLinkedLoss_FixesItsPricesAsItIsLinked()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, (Tritanium, 5), (Rifter, 1_000), (Hobgoblin, 10));
        Guid runId = await _SaveRunAsync(instance, Pilot);
        await _AddLossAsync(instance, 1);

        await _Dispatcher(instance).Send(new LinkKillmailsToRunsCommand((int)Pilot), Token);

        Assert.Equal([(runId, Rifter, 1_000m), (runId, Hobgoblin, 10m)],
            (await _LossPricesAsync(instance)).Select(price => (price.RunId, price.TypeId, price.UnitPriceIsk.GetValueOrDefault())));
    }

    /// <summary>ET-464 criterion 2 and the republish pitfall. Red if an unpriced item is never filled, if a fixed one moves,
    /// or if pricing a loss marks a published run as changed since it was published.</summary>
    [AvaloniaFact]
    public async Task Fill_GivesAnUnpricedLossItemTheFirstPriceOnce_WithoutMarkingThePublishedRunChanged()
    {
        using TestClientInstance instance = _Instance();
        await _AddLocalCharacterAsync(instance, Pilot);
        await _PriceAsync(instance, (Tritanium, 5), (Rifter, 1_000));
        Guid runId = await _SaveRunAsync(instance, Pilot);
        await _AddLossAsync(instance, 1);
        await _Dispatcher(instance).Send(new SetKillmailRunLinkCommand((int)Pilot, 1, runId), Token);
        await _MarkPublishedAsync(instance, runId);
        int revision = (await _RunAsync(instance, runId)).Revision;
        await _PriceAsync(instance, (Tritanium, 5), (Rifter, 9_000), (Hobgoblin, 10));

        Result<int> filled = await _Dispatcher(instance).Send(new FillRunPriceSnapshotsCommand(), Token);
        await _PriceAsync(instance, (Tritanium, 5), (Rifter, 9_000), (Hobgoblin, 90));
        Result<int> again = await _Dispatcher(instance).Send(new FillRunPriceSnapshotsCommand(), Token);

        Assert.Equal((1, 0), (filled.Value, again.Value));
        Assert.Equal([(Rifter, 1_000m, PriceSnapshotSource.Capture), (Hobgoblin, 10m, PriceSnapshotSource.Backfill)],
            (await _LossPricesAsync(instance)).Select(price => (price.TypeId, price.UnitPriceIsk.GetValueOrDefault(), price.PriceSource)));
        Assert.Equal(100 * 5m - (1_000m + 3 * 10m), (await _SummaryAsync(instance)).TotalIsk);
        Run run = await _RunAsync(instance, runId);
        Assert.Equal((RunSyncState.Synced, revision), (run.SyncState, run.Revision));
    }

    /// <summary>ET-464 criterion 3. Red if "Re-value at current prices" leaves the loss at its old price, or if a run whose
    /// loss alone moved is marked as changed since it was published.</summary>
    [AvaloniaFact]
    public async Task Revalue_SetsTheLossToTheCurrentPrices_WithoutMarkingThePublishedRunChanged()
    {
        using TestClientInstance instance = _Instance();
        await _AddLocalCharacterAsync(instance, Pilot);
        await _PriceAsync(instance, (Tritanium, 5), (Rifter, 1_000), (Hobgoblin, 10));
        Guid runId = await _SaveRunAsync(instance, Pilot);
        await _AddLossAsync(instance, 1);
        await _Dispatcher(instance).Send(new SetKillmailRunLinkCommand((int)Pilot, 1, runId), Token);
        await _MarkPublishedAsync(instance, runId);
        int revision = (await _RunAsync(instance, runId)).Revision;
        await _PriceAsync(instance, (Tritanium, 5), (Rifter, 2_000), (Hobgoblin, 20));

        Result<int> revalued = await _Dispatcher(instance).Send(new RevalueRunsCommand([runId]), Token);

        Assert.Equal(1, revalued.Value);
        Assert.All(await _LossPricesAsync(instance), price => Assert.Equal(PriceSnapshotSource.Revalued, price.PriceSource));
        Assert.Equal(100 * 5m - (2_000m + 3 * 20m), (await _SummaryAsync(instance)).TotalIsk);
        Run run = await _RunAsync(instance, runId);
        Assert.Equal((RunSyncState.Synced, revision), (run.SyncState, run.Revision));
    }

    /// <summary>ET-464 criterion 4. Red if the run a loss leaves keeps its prices, or if the run it joins takes them over
    /// rather than pricing the loss afresh.</summary>
    [AvaloniaFact]
    public async Task MovingALoss_DropsItsPricesFromTheOldRun_AndPricesItAfreshOnTheNewOne()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, (Tritanium, 5), (Rifter, 1_000), (Hobgoblin, 10));
        Guid first = await _SaveRunAsync(instance, Pilot);
        Guid second = await _SaveRunAsync(instance, Pilot);
        await _AddLossAsync(instance, 1);
        await _Dispatcher(instance).Send(new SetKillmailRunLinkCommand((int)Pilot, 1, first), Token);
        await _PriceAsync(instance, (Tritanium, 5), (Rifter, 2_000), (Hobgoblin, 20));

        await _Dispatcher(instance).Send(new SetKillmailRunLinkCommand((int)Pilot, 1, second), Token);

        Assert.Equal([(second, Rifter, 2_000m), (second, Hobgoblin, 20m)],
            (await _LossPricesAsync(instance)).Select(price => (price.RunId, price.TypeId, price.UnitPriceIsk.GetValueOrDefault())));

        await _Dispatcher(instance).Send(new SetKillmailRunLinkCommand((int)Pilot, 1, null), Token);

        Assert.Empty(await _LossPricesAsync(instance));
    }

    /// <summary>ET-464. Red if fixing a loss's prices stays silent, or if a pass that changes nothing still signals.</summary>
    [AvaloniaFact]
    public async Task SnapshotLossPrices_SignalsTheRunWhosePricesChanged_AndNothingWhenNoneDid()
    {
        using TestClientInstance instance = _Instance();
        Guid runId = await _SaveRunAsync(instance, Pilot);
        await _AddLossAsync(instance, 1, runId);
        List<Guid?> runsChanged = [];
        using IDisposable listening = instance.Services.GetRequiredService<IEventBus>()
            .Subscribe<RunsChangedEvent>(changed => runsChanged.Add(changed.Data.RunId));

        Result<int> first = await _Dispatcher(instance).Send(new SnapshotRunLossPricesCommand([runId]), Token);
        Result<int> second = await _Dispatcher(instance).Send(new SnapshotRunLossPricesCommand([runId]), Token);

        Assert.Equal((1, 0), (first.Value, second.Value));
        Assert.Equal([runId], runsChanged);
    }

    /// <summary>ET-464 criterion 6. Red if a loss linked before the update keeps being valued live, or if fixing its prices
    /// sends the published run to the server again.</summary>
    [AvaloniaFact]
    public async Task Fill_FixesALossLinkedBeforeTheUpdate_AsMigrated_WithoutMarkingThePublishedRunChanged()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, (Tritanium, 5), (Rifter, 1_000), (Hobgoblin, 10));
        Guid runId = await _SaveRunAsync(instance, Pilot);
        await _AddLossAsync(instance, 1, runId);
        await _MarkPublishedAsync(instance, runId);
        int revision = (await _RunAsync(instance, runId)).Revision;

        Result<int> filled = await _Dispatcher(instance).Send(new FillRunPriceSnapshotsCommand(), Token);
        await _PriceAsync(instance, (Tritanium, 5), (Rifter, 9_000), (Hobgoblin, 90));
        await _RebuildAsync(instance);

        Assert.Equal(1, filled.Value);
        Assert.All(await _LossPricesAsync(instance), price => Assert.Equal(PriceSnapshotSource.Migrated, price.PriceSource));
        Assert.Equal(100 * 5m - (1_000m + 3 * 10m), (await _SummaryAsync(instance)).TotalIsk);
        Run run = await _RunAsync(instance, runId);
        Assert.Equal((RunSyncState.Synced, revision), (run.SyncState, run.Revision));
    }

    private static TestClientInstance _Instance() =>
        TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
            .Add(Tritanium, "Tritanium", 18, 4)
            .Add(Pyerite, "Pyerite", 18, 4)
            .Add(Veldspar, "Veldspar", 462, 25)
            .Add(Filament, "Agitated Dark Filament", 1979, 8)));

    private static IDispatcher _Dispatcher(TestClientInstance instance) => instance.Services.GetRequiredService<IDispatcher>();

    private static Task _PriceAsync(TestClientInstance instance, params (int TypeId, double Price)[] prices) =>
        instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [.. prices.Select(price => new LocalMarketPrice
            {
                TypeId = price.TypeId, AveragePrice = price.Price, AdjustedPrice = price.Price, UpdatedAt = DateTimeOffset.UtcNow
            })], Token);

    /// <summary>A saved run with 100 Tritanium, and the extra line beside it when one is named.</summary>
    private static async Task<Guid> _SaveRunAsync(TestClientInstance instance, long characterId, (int TypeId, long Quantity)? extra = null)
    {
        IDispatcher dispatcher = _Dispatcher(instance);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, "Blood Refuge", 30000142), Token);
        Assert.True(started.IsSuccess);
        List<RunLootEntryInput> entries =
        [
            new RunLootEntryInput { ItemTypeId = Tritanium, Name = "Tritanium", Quantity = 100, LootKind = LootKind.Gained }
        ];
        if (extra is { } line)
            entries.Add(new RunLootEntryInput { ItemTypeId = line.TypeId, Name = $"Type {line.TypeId}", Quantity = line.Quantity, LootKind = LootKind.Gained });
        Assert.True((await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
        [
            new RunLootCaptureInput
            {
                CapturedAtUtc = StartedAtUtc.AddMinutes(10), Source = LootCaptureSource.Pasted, CharacterId = characterId, Entries = entries
            }
        ], [], [], []), Token)).IsSuccess);
        return started.Value;
    }

    private static async Task _RebuildAsync(TestClientInstance instance) =>
        Assert.True((await _Dispatcher(instance).Send(new RebuildActivitySummariesCommand(), Token)).IsSuccess);

    private static async Task _MigrateAsync(TestClientInstance instance, string? target)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(target, Token);
    }

    private static async Task _AddLocalCharacterAsync(TestClientInstance instance, long characterId)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        db.Set<LocalCharacter>().Add(new LocalCharacter { EsiCharacterId = checked((int)characterId), Name = "Pilot" });
        await db.SaveChangesAsync(Token);
    }

    private static async Task<ActivitySummary> _SummaryAsync(TestClientInstance instance)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        return Assert.Single(await db.Set<ActivitySummary>().AsNoTracking().ToListAsync(Token));
    }

    private static async Task<List<RunLootEntry>> _EntriesAsync(TestClientInstance instance)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        return await db.Set<RunLootEntry>().AsNoTracking().ToListAsync(Token);
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

    /// <summary>A Rifter lost with three Hobgoblins aboard (two destroyed, one dropped), linked to a run straight in the
    /// store when one is named, as a link made before ET-464 stands.</summary>
    private static async Task _AddLossAsync(TestClientInstance instance, int killmailId, Guid? linkedRunId = null)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        db.Set<LocalKillmail>().Add(new LocalKillmail
        {
            CharacterId = (int)Pilot, KillmailId = killmailId, Hash = $"hash{killmailId}", KillmailTimeUtc = StartedAtUtc.AddMinutes(5),
            SolarSystemId = 30000142, IsLoss = true, VictimShipTypeId = Rifter, VictimCharacterId = (int)Pilot,
            RunId = linkedRunId, LinkSource = linkedRunId is null ? KillmailLinkSource.None : KillmailLinkSource.Auto,
            ImportedAtUtc = DateTime.UtcNow,
            Items = [new LocalKillmailItem { Flag = 87, TypeId = Hobgoblin, QuantityDestroyed = 2, QuantityDropped = 1 }]
        });
        await db.SaveChangesAsync(Token);
    }

    private static async Task<List<RunLossPrice>> _LossPricesAsync(TestClientInstance instance)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        return await db.Set<RunLossPrice>().AsNoTracking()
            .OrderBy(price => price.RunId).ThenBy(price => price.TypeId != Rifter)
            .ToListAsync(Token);
    }

    private static Task<ClientDbContext> _DbAsync(TestClientInstance instance) =>
        instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);
}
