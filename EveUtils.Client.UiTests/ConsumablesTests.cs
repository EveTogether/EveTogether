using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Runs.Queries;
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
        ActivityConsumableRowViewModel consumableRow = Assert.Single(consumables.Rows);
        Assert.Equal("2x", consumableRow.CountText);
        Assert.Equal("-10,000,000 ISK", consumables.CostText);
    }
}
