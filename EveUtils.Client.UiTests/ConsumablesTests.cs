using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Esi;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-249 — CONSUMABLES: an abyssal filament's cost, registered as a negative ISK contribution (ET-256) rather than
/// a second formula anywhere. The hull-class count proposal is data, not a guess: frigate, destroyer and cruiser
/// (Jithran, 2026-09-11 and ET-263 2026-09-12) have a confirmed rule, every other hull class proposes nothing until
/// someone measures the SDE.
/// </summary>
public sealed class ConsumablesTests
{
    // ── The hull-class rule is data, and only proposes what has been measured ─────────────────────────

    [Theory]
    [InlineData("Frigate", 3)]
    [InlineData("Destroyer", 2)]
    [InlineData("Cruiser", 1)]
    [InlineData("Battlecruiser", null)]
    [InlineData(null, null)]
    public void ProposedCount_OnlyAnswersForAMeasuredHullClass(string? hullClass, int? expected) =>
        Assert.Equal(expected, AbyssalConsumables.ProposedCount(hullClass));

    // ── The filament type is resolved by exact name, never guessed ────────────────────────────────────

    [Fact]
    public void ResolveTypeId_FindsTheExactFilamentName()
    {
        var sde = new FakeSdeAccessor().Add(60000, "Agitated Dark Filament", 1979, 8);
        Assert.Equal(60000, AbyssalConsumables.ResolveTypeId(sde, tierIndex: 2, weatherName: "Dark"));
    }

    [Fact]
    public void ResolveTypeId_IsNull_WhenTheSdeHasNoTypeByThatName()
    {
        var sde = new FakeSdeAccessor();
        Assert.Null(AbyssalConsumables.ResolveTypeId(sde, tierIndex: 2, weatherName: "Dark"));
    }

    [Fact]
    public void ResolveTypeId_IsNull_WhenTheSdeIsUnavailable()
    {
        var sde = new FakeSdeAccessor().Add(60000, "Agitated Dark Filament", 1979, 8).Offline();
        Assert.Null(AbyssalConsumables.ResolveTypeId(sde, tierIndex: 2, weatherName: "Dark"));
    }

    // ── The contributor: negative, and honest about what it does not know ─────────────────────────────

    [Fact]
    public void ConsumableIskContributor_IsNegative_WhenTheCostIsKnown()
    {
        RunIskFacts facts = _Facts(cost: 5_000_000m, hasConsumables: true);
        IskContribution? contribution = new ConsumableIskContributor().Contribute([facts], DateTime.UtcNow);
        Assert.Equal(-5_000_000m, contribution!.Amount);
        Assert.Equal(IskCertainty.Measured, contribution.Certainty);
    }

    [Fact]
    public void ConsumableIskContributor_IsUnknownNotZero_WhenACountWasConfirmedButNotPriced()
    {
        RunIskFacts facts = _Facts(cost: null, hasConsumables: true);
        IskContribution? contribution = new ConsumableIskContributor().Contribute([facts], DateTime.UtcNow);
        Assert.Equal(0m, contribution!.Amount);
        Assert.Equal(IskCertainty.Unknown, contribution.Certainty);
    }

    [Fact]
    public void ConsumableIskContributor_IsNull_WhenNothingWasEverConfirmed()
    {
        RunIskFacts facts = _Facts(cost: null, hasConsumables: false);
        Assert.Null(new ConsumableIskContributor().Contribute([facts], DateTime.UtcNow));
    }

    private static RunIskFacts _Facts(decimal? cost, bool hasConsumables) => new()
    {
        CharacterId = 1,
        BountyIsk = 0m,
        LootIskNet = null,
        HasLoot = false,
        ConsumableIskCost = cost,
        HasConsumables = hasConsumables,
        MiningIskValue = null,
        HasMining = false,
        Parameters = [],
        StoppedAtUtc = null,
        HomefrontExpectedPayoutIsk = null
    };

    // ── The catalogue claims it only for an abyssal ────────────────────────────────────────────────────

    [Fact]
    public void Abyssal_ClaimsConsumables_OnBothScreens()
    {
        RunTypeDefinition abyssal = RunTypeCatalogue.For(RunTypeId.Abyssal);
        Assert.Contains(RunSectionId.Consumables, abyssal.WindowSections);
        Assert.Contains(RunSectionId.Consumables, abyssal.DetailSections);
    }

    [Fact]
    public void ASite_DoesNotClaimConsumables()
    {
        RunTypeDefinition site = RunTypeCatalogue.For(RunTypeId.CombatSite);
        Assert.DoesNotContain(RunSectionId.Consumables, site.WindowSections);
        Assert.DoesNotContain(RunSectionId.Consumables, site.DetailSections);
    }

    // ── End to end: a saved abyssal's filament cost lowers TOTAL ISK, and never as a reward ────────────

    /// <summary>
    /// Counter-proof: before this ticket, no code path ever wrote <c>RunParameterKey.AbyssalFilamentCount</c> or
    /// <c>AbyssalFilamentTypeId</c>, so this table had zero rows for either — <see cref="ConsumableIskContributor"/>
    /// registered but never fed. SAVE is what commits both; the registry (ET-256) picks the new source up without
    /// the overview row, the detail screen or "ISK today" being touched, and it is never offered as a reward chip.
    /// </summary>
    [AvaloniaFact]
    public async Task SavingAnAbyssal_WithAConfirmedFilamentCount_LowersTotalIsk_AndIsNeverAReward()
    {
        using var instance = TestClientInstance.Create();
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            new LocalMarketPrice { TypeId = 60000, AveragePrice = 5_000_000, AdjustedPrice = 5_000_000, UpdatedAt = DateTimeOffset.UtcNow }
        ]);

        DateTime startedAtUtc = new(2026, 9, 12, 20, 0, 0, DateTimeKind.Utc);
        Result<Guid> started = await dispatcher.Send(
            new StartRunCommand(90000001, ActivityKind.Abyssal, startedAtUtc, 0, null, null));
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(20), startedAtUtc.AddMinutes(21),
            [], [], [],
            [
                new RunParameterInput
                {
                    ParameterKey = RunParameterKey.AbyssalFilament, TypedValue = "2|Dark", ObservedAtUtc = startedAtUtc
                },
                new RunParameterInput
                {
                    ParameterKey = RunParameterKey.AbyssalFilamentTypeId,
                    TypedValue = 60000.ToString(CultureInfo.InvariantCulture), ObservedAtUtc = startedAtUtc
                },
                new RunParameterInput
                {
                    ParameterKey = RunParameterKey.AbyssalFilamentCount, TypedValue = "2", ObservedAtUtc = startedAtUtc
                }
            ]));

        ActivityOverviewRowDto row = Assert.Single((await dispatcher.Query(new GetActivityOverviewQuery())).Value!);
        Assert.Equal(-10_000_000m, row.Isk.Of(IskSource.Consumables)!.Amount);
        Assert.Equal(-10_000_000m, row.Isk.Total);
        Assert.DoesNotContain(row.Rewards, reward =>
            reward.ParameterKey is RunParameterKey.AbyssalFilamentTypeId or RunParameterKey.AbyssalFilamentCount);

        var detail = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId);
        await detail.LoadAsync();
        var consumables = (ConsumablesDetailSectionViewModel)Assert.Single(
            detail.Sections, section => section is ConsumablesDetailSectionViewModel);
        ActivityLootLineViewModel consumableRow = Assert.Single(consumables.Rows);
        Assert.Equal("2×", consumableRow.QuantityText);
        Assert.Equal("-10,000,000 ISK", consumables.CostText);
    }

    // ── ET-329: the filament is spent like any other line, and is taken off the loot once ──────────────

    /// <summary>
    /// Jithran's Chaotic Exotic run: LOOT said 53,060,238, CONSUMED said "no price" and NET repeated LOOT although the
    /// overview total already had the filament off. Red without the change: CONSUMED read "no price", NET equalled LOOT,
    /// and CONSUMABLES held a bare count with no type, so no icon and no name.
    /// </summary>
    [AvaloniaFact]
    public async Task SavedAbyssal_ShowsTheFilamentAsALootLine_AndConsumedAndNetAgreeWithTheOverviewTotal()
    {
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<ISdeAccessor>(_Sde()));
        await _PriceAsync(instance, (34, 10_000_000), (60000, 5_000_000));
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _SaveAbyssalAsync(dispatcher, filamentCount: 2, lootQuantity: 3);

        ActivityOverviewRowDto row = Assert.Single((await dispatcher.Query(new GetActivityOverviewQuery())).Value!);
        ActivityDetailViewModel detail = await _DetailAsync(instance, dispatcher, row);

        ActivityLootLineViewModel filament = Assert.Single(detail.Consumables().Rows);
        Assert.Equal(60000, filament.ItemTypeId);
        Assert.Equal("Agitated Dark Filament", filament.Name);
        Assert.Equal("2×", filament.QuantityText);
        Assert.Equal("10,000,000", filament.AmountText);
        Assert.Equal("Test Pilot", filament.OwnerText);
        Assert.True(filament.IsLost);

        ActivityLootViewModel loot = detail.Loot().LootOverview;
        Assert.Equal("30,000,000 ISK", loot.LootIskDisplay);
        Assert.Equal("-10,000,000 ISK", loot.ConsumedIskDisplay);
        Assert.Equal("20,000,000 ISK", loot.NetIskDisplay);
        Assert.Equal("30,000,000 ISK", detail.Loot().HeaderSummary.Split(" · ")[0]);
        Assert.Equal(20_000_000m, row.Isk.Total);
        Assert.Equal("20,000,000 ISK", detail.TotalIskText);
    }

    /// <summary>
    /// The 62,789 ISK between the loot header and LOOT: the header is the summary's stored share, valued when the run
    /// was saved, the table under it is valued at today's prices. Red without the change: the prices moved from 100 to
    /// 200 after the save and the header kept reading 300 next to a table that said 600.
    /// </summary>
    [AvaloniaFact]
    public async Task OpeningASavedActivity_AddsItUpAgain_WhenThePricesMovedSinceItWasSaved()
    {
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<ISdeAccessor>(_Sde()));
        await _PriceAsync(instance, (34, 100));
        IDispatcher dispatcher = instance.Services.GetRequiredService<IDispatcher>();
        await _SaveAbyssalAsync(dispatcher, filamentCount: 0, lootQuantity: 3);
        await _PriceAsync(instance, (34, 200));

        ActivityOverviewRowDto row = Assert.Single((await dispatcher.Query(new GetActivityOverviewQuery())).Value!);
        ActivityDetailViewModel detail = await _DetailAsync(instance, dispatcher, row);

        Assert.Equal("600 ISK", detail.Loot().LootOverview.LootIskDisplay);
        Assert.StartsWith("600 ISK", detail.Loot().HeaderSummary);
        Assert.Equal("600 ISK", detail.TotalIskText);
        Assert.Equal(600m, Assert.Single((await dispatcher.Query(new GetActivityOverviewQuery())).Value!).Isk.Total);
    }

    /// <summary>The run window hands the filament its rows price to the LOOT totals, so CONSUMED and NET there say the
    /// same as the saved run does. Red without the change: the loot totals never heard of the filament.</summary>
    [AvaloniaFact]
    public async Task RunWindow_HandsTheFilamentCostToTheLootTotals()
    {
        var cruiserFit = new ShipFitDetectionReading(ShipFitDetectionState.Observed, DateTimeOffset.UtcNow, null, null, null,
            new ShipFitCandidate(1, "Chosen fit", 620), ShipFitMatchReason.Manual, []);
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services =>
        {
            services.AddSingleton<ISdeAccessor>(_Sde().Add(620, "Cruiser", 26, 6, groupName: "Cruiser"));
            services.AddSingleton<IShipFitDetectionService>(new FixedFitDetection(cruiserFit));
        });
        await _PriceAsync(harness.Instance, (60000, 5_000_000));
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Abyssal);
        await model.Activity().SelectTierCommand.ExecuteAsync(2);
        await model.Activity().SelectWeatherCommand.ExecuteAsync(0);
        await model.StartRunCommand.ExecuteAsync(null);

        await ActivityWindowHarness.WaitUntil(() =>
        {
            model.Refresh(DateTime.UtcNow);
            return model.LootOverview?.FilamentIsk is not null;
        });

        Assert.Equal(5_000_000m, model.LootOverview!.FilamentIsk);
        Assert.Equal("-5,000,000 ISK", model.LootOverview.ConsumedIskDisplay);
    }

    private static FakeSdeAccessor _Sde() => new FakeSdeAccessor()
        .Add(60000, "Agitated Dark Filament", 1979, 8)
        .Add(34, "Tritanium", 18, 4);

    private static Task _PriceAsync(TestClientInstance instance, params (int TypeId, double Price)[] prices) =>
        instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            .. prices.Select(price => new LocalMarketPrice
            {
                TypeId = price.TypeId, AveragePrice = price.Price, AdjustedPrice = price.Price, UpdatedAt = DateTimeOffset.UtcNow
            })
        ]);

    private static async Task _SaveAbyssalAsync(IDispatcher dispatcher, int filamentCount, long lootQuantity)
    {
        DateTime startedAtUtc = new(2026, 9, 22, 18, 0, 0, DateTimeKind.Utc);
        Result<Guid> started = await dispatcher.Send(
            new StartRunCommand(90000001, ActivityKind.Abyssal, startedAtUtc, 0, null, null));
        List<RunParameterInput> parameters =
        [
            new() { ParameterKey = RunParameterKey.AbyssalFilament, TypedValue = "2|Dark", ObservedAtUtc = startedAtUtc },
            new()
            {
                ParameterKey = RunParameterKey.AbyssalFilamentTypeId, TypedValue = "60000", ObservedAtUtc = startedAtUtc
            }
        ];
        if (filamentCount > 0)
            parameters.Add(new RunParameterInput
            {
                ParameterKey = RunParameterKey.AbyssalFilamentCount,
                TypedValue = filamentCount.ToString(CultureInfo.InvariantCulture),
                ObservedAtUtc = startedAtUtc
            });

        Result saved = await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(20),
            startedAtUtc.AddMinutes(21),
            [
                new RunLootCaptureInput
                {
                    CapturedAtUtc = startedAtUtc.AddMinutes(10), Source = LootCaptureSource.Clipboard, ContentHash = "loot",
                    Entries = [new RunLootEntryInput { ItemTypeId = 34, Name = "Tritanium", Quantity = lootQuantity, LootKind = LootKind.Gained }]
                }
            ], [], [], [.. parameters]));
        Assert.True(saved.IsSuccess);
    }

    private static async Task<ActivityDetailViewModel> _DetailAsync(
        TestClientInstance instance, IDispatcher dispatcher, ActivityOverviewRowDto row)
    {
        var detail = new ActivityDetailViewModel(dispatcher, row.ActivitySummaryId,
            instance.Services.GetRequiredService<IAppraisalProvider>(), _ => "Test Pilot",
            sde: instance.Services.GetRequiredService<ISdeAccessor>());
        await detail.LoadAsync();
        return detail;
    }

    // ── ET-328: the proposed count comes from the fit chosen for the run ───────────────────────────────

    /// <summary>
    /// Red without the change: the count was read from the ship the live detection last saw, so a run with a chosen fit
    /// and no live ship proposed nothing at all.
    /// </summary>
    [Theory]
    [InlineData(587, "Frigate", 25, 3)]
    [InlineData(16240, "Destroyer", 420, 2)]
    [InlineData(620, "Cruiser", 26, 1)]
    public async Task ProposedCount_ComesFromTheChosenFit_WhenTheLiveDetectionSeesNoShip(
        int shipTypeId, string hullClass, int groupId, int expected)
    {
        var fitOnly = new ShipFitDetectionReading(ShipFitDetectionState.Observed, DateTimeOffset.UtcNow, null, null, null,
            new ShipFitCandidate(1, "Chosen fit", shipTypeId), ShipFitMatchReason.Manual, []);

        Assert.Equal(expected, await _ProposedCountAsync(fitOnly, shipTypeId, hullClass, groupId));
    }

    /// <summary>
    /// The live ship stays the source when no fit is chosen. Covered nowhere before, so it stands as the second test
    /// of the ticket's budget.
    /// </summary>
    [Fact]
    public async Task ProposedCount_FallsBackToTheLiveShip_WhenNoFitIsChosen()
    {
        var liveOnly = new ShipFitDetectionReading(ShipFitDetectionState.Observed, DateTimeOffset.UtcNow, 620, 9, "Vexor",
            null, ShipFitMatchReason.NoFitFound, []);

        Assert.Equal(1, await _ProposedCountAsync(liveOnly, 620, "Cruiser", 26));
    }

    private static async Task<int?> _ProposedCountAsync(
        ShipFitDetectionReading reading, int shipTypeId, string hullClass, int groupId)
    {
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services =>
        {
            services.AddSingleton<ISdeAccessor>(_Sde().Add(shipTypeId, hullClass, groupId, 6, groupName: hullClass));
            services.AddSingleton<IShipFitDetectionService>(new FixedFitDetection(reading));
        });
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Abyssal);
        await model.Activity().SelectTierCommand.ExecuteAsync(2);
        await model.Activity().SelectWeatherCommand.ExecuteAsync(0);
        await model.StartRunCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() =>
        {
            model.Refresh(DateTime.UtcNow);
            return model.Consumables().Rows.Count > 0;
        });

        return Assert.Single(model.Consumables().Rows).Count;
    }

    private sealed class FixedFitDetection(ShipFitDetectionReading reading) : IShipFitDetectionService
    {
        public ShipFitDetectionReading GetReading(int characterId) => reading;

        public Task<Result> SetManualFitAsync(int characterId, int? fittingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> DetachFitAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }
}
