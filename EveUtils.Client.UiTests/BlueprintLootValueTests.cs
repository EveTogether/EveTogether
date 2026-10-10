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
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Market.Services.Implementations;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Settings.Commands;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-501: a blueprint in loot has no market price of its own, so it is valued at what building it once earns at EVE
/// average prices — product minus materials minus a job cost of 9% of the estimated item value — floored at 0, fixed on
/// the line like any other price. Headless, against a temp store.
/// </summary>
public sealed class BlueprintLootValueTests
{
    private const int Blueprint = 85957;
    private const int Product = 85474;
    private const int Mutaplasmid = 47742;
    private const int AstronauticResidue = 85391;
    private const int MediumResidue = 85398;
    private const long Pilot = 90000001;
    private static readonly DateTime StartedAtUtc = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    // Materials 1 × 100,000 + 60 × 1,000 + 60 × 500 = 190,000; the same at adjusted prices, so the job is 9% of it.
    private const decimal MaterialsCost = 190_000m;
    private const decimal JobCost = MaterialsCost * 0.09m;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task SaveRun_ValuesABlueprintLine_AtProductMinusMaterialsMinusJobCost()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, productPrice: 1_000_000);

        await _SaveRunAsync(instance, quantity: 2);

        RunLootEntry entry = Assert.Single(await _EntriesAsync(instance));
        Assert.Equal(1_000_000m - MaterialsCost - JobCost, entry.UnitPriceIsk);
        Assert.Equal((LootPriceBasis.BlueprintAppraisal, PriceSnapshotSource.Capture), (entry.PriceBasis, entry.PriceSource));
    }

    [AvaloniaFact]
    public async Task SaveRun_LeavesABlueprintUnpriced_WhenBlueprintValuationIsOff()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, productPrice: 1_000_000);
        await _Dispatcher(instance).Send(new SetSettingCommand(BlueprintAppraisalService.LootValuationSettingKey, "false"), Token);

        await _SaveRunAsync(instance);

        RunLootEntry entry = Assert.Single(await _EntriesAsync(instance));
        Assert.Null(entry.UnitPriceIsk);
    }

    [AvaloniaFact]
    public async Task SaveRun_ValuesALossMakingBlueprintAtZero()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, productPrice: 100_000);

        await _SaveRunAsync(instance);

        RunLootEntry entry = Assert.Single(await _EntriesAsync(instance));
        Assert.Equal((0m, LootPriceBasis.BlueprintAppraisal), (entry.UnitPriceIsk, entry.PriceBasis));
    }

    [AvaloniaFact]
    public async Task SaveRun_LeavesABlueprintUnpriced_WhenAMaterialHasNoPrice_EvenWithAMarketPriceForTheBlueprint()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, productPrice: 1_000_000, withMediumResidue: false, blueprintMarketPrice: 5_000_000);

        await _SaveRunAsync(instance);

        RunLootEntry entry = Assert.Single(await _EntriesAsync(instance));
        Assert.Null(entry.UnitPriceIsk);
    }

    [AvaloniaFact]
    public async Task Fill_ValuesABlueprintLineThatStayedUnpriced_OnceValuationIsOn()
    {
        using TestClientInstance instance = _Instance();
        await _AddLocalCharacterAsync(instance);
        await _PriceAsync(instance, productPrice: 1_000_000);
        IDispatcher dispatcher = _Dispatcher(instance);
        await dispatcher.Send(new SetSettingCommand(BlueprintAppraisalService.LootValuationSettingKey, "false"), Token);
        await _SaveRunAsync(instance);
        await dispatcher.Send(new SetSettingCommand(BlueprintAppraisalService.LootValuationSettingKey, "true"), Token);

        Result<int> filled = await dispatcher.Send(new FillRunPriceSnapshotsCommand(), Token);

        Assert.Equal(1, filled.Value);
        RunLootEntry entry = Assert.Single(await _EntriesAsync(instance));
        Assert.Equal((1_000_000m - MaterialsCost - JobCost, PriceSnapshotSource.Backfill, LootPriceBasis.BlueprintAppraisal),
            (entry.UnitPriceIsk, entry.PriceSource, entry.PriceBasis));
    }

    [AvaloniaFact]
    public async Task Appraise_MultipliesTheBuildByItsRuns()
    {
        using TestClientInstance instance = _Instance();
        await _PriceAsync(instance, productPrice: 1_000_000);
        IBlueprintAppraisalService appraisals = instance.Services.CreateScope().ServiceProvider
            .GetRequiredService<IBlueprintAppraisalService>();

        BlueprintAppraisal? appraisal = await appraisals.AppraiseAsync(Blueprint, runs: 3, materialEfficiency: 0, Token);

        Assert.NotNull(appraisal);
        Assert.Equal(3 * (1_000_000m - MaterialsCost - JobCost), appraisal.Value);
        Assert.Equal([3L, 180L, 180L], appraisal.Materials.Select(material => material.Needed));
    }

    [AvaloniaFact]
    public async Task LootLine_SaysItsValueIsABlueprintAppraisal()
    {
        using TestClientInstance instance = _Instance();
        var viewModel = new RunLootViewModel(_Dispatcher(instance));

        await viewModel.LoadAsync(
        [
            new RunLootCaptureDto(Guid.NewGuid(), StartedAtUtc, IsExcluded: false, ContentHash: null, LootCaptureSource.Pasted,
                LootCaptureRole.Snapshot,
                [new RunLootEntryDto(Blueprint, "Blueprint", 1, null, LootKind.Gained, 792_900m, LootPriceBasis.BlueprintAppraisal)])
        ], Token);

        ActivityLootLineViewModel line = Assert.Single(viewModel.ItemRows);
        Assert.Equal((true, false), (line.IsBlueprintAppraisal, line.IsLivePrice));
        Assert.DoesNotContain("appraisal", line.ValueText);
    }

    private static TestClientInstance _Instance() =>
        TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
            .Add(Blueprint, "Glorified Decayed 50MN Microwarpdrive Mutaplasmid Blueprint", 4337, 9)
            .AddBlueprint(new SdeBlueprintManufacturing(Blueprint, Product, ProductQuantity: 1, TimeSeconds: 3600,
                MaxProductionLimit: 10,
                [
                    new SdeBlueprintMaterial(Mutaplasmid, 1),
                    new SdeBlueprintMaterial(AstronauticResidue, 60),
                    new SdeBlueprintMaterial(MediumResidue, 60)
                ]))));

    private static IDispatcher _Dispatcher(TestClientInstance instance) => instance.Services.GetRequiredService<IDispatcher>();

    private static Task _PriceAsync(TestClientInstance instance, double productPrice, bool withMediumResidue = true,
        double? blueprintMarketPrice = null)
    {
        List<(int TypeId, double Price)> prices = [(Product, productPrice), (Mutaplasmid, 100_000), (AstronauticResidue, 1_000)];
        if (withMediumResidue)
            prices.Add((MediumResidue, 500));
        if (blueprintMarketPrice is { } market)
            prices.Add((Blueprint, market));
        return instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
            [.. prices.Select(price => new LocalMarketPrice
            {
                TypeId = price.TypeId, AveragePrice = price.Price, AdjustedPrice = price.Price, UpdatedAt = DateTimeOffset.UtcNow
            })], Token);
    }

    private static async Task _SaveRunAsync(TestClientInstance instance, long quantity = 1)
    {
        IDispatcher dispatcher = _Dispatcher(instance);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(Pilot, ActivityKind.Site, StartedAtUtc,
            1234, "Blood Refuge", 30000142), Token);
        Assert.True(started.IsSuccess);
        Assert.True((await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15), StartedAtUtc.AddMinutes(16),
        [
            new RunLootCaptureInput
            {
                CapturedAtUtc = StartedAtUtc.AddMinutes(10), Source = LootCaptureSource.Pasted, CharacterId = Pilot,
                Entries = [new RunLootEntryInput { ItemTypeId = Blueprint, Name = "Blueprint", Quantity = quantity, LootKind = LootKind.Gained }]
            }
        ], [], [], []), Token)).IsSuccess);
    }

    private static async Task _AddLocalCharacterAsync(TestClientInstance instance)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        db.Set<LocalCharacter>().Add(new LocalCharacter { EsiCharacterId = checked((int)Pilot), Name = "Pilot" });
        await db.SaveChangesAsync(Token);
    }

    private static async Task<List<RunLootEntry>> _EntriesAsync(TestClientInstance instance)
    {
        await using ClientDbContext db = await _DbAsync(instance);
        return await db.Set<RunLootEntry>().AsNoTracking().ToListAsync(Token);
    }

    private static Task<ClientDbContext> _DbAsync(TestClientInstance instance) =>
        instance.Services.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(Token);
}
