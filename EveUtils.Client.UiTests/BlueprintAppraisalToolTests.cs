using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Market.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-502: the Appraisal tool's BLUEPRINTS mode values pasted blueprints with the ET-501 calculation at EVE average
/// prices, pre-checks the profitable ones, sums the checked ones' materials into a shopping list that copies as EVE
/// multibuy, and a blueprint loot line opens it. Headless, against a temp store with a fake SDE.
/// </summary>
public sealed class BlueprintAppraisalToolTests
{
    private const int Tritanium = 34;
    private const int Pyerite = 35;
    private const int Hobgoblin = 2454;
    private const int HobgoblinBlueprint = 2455;
    private const int Repairer = 523;
    private const int RepairerBlueprint = 524;
    private const int Analyzer = 600;
    private const int AnalyzerBlueprint = 601;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Hobgoblin: 100 Tritanium + 50 Pyerite = 100 × 5 + 50 × 10 = 1,000; job 9% = 90; product 2,000 → profit 910.
    // Repairer: 200 Tritanium = 1,000; job 90; product 500 → loss 590. Analyzer: its product has no price.
    private const decimal HobgoblinProfit = 2_000m - 1_000m - 90m;
    private const decimal RepairerProfit = 500m - 1_000m - 90m;

    [AvaloniaFact]
    public async Task Appraise_ValuesEachBlueprint_AndPreChecksOnlyTheProfitableOne()
    {
        using TestClientInstance instance = await _InstanceAsync();
        BlueprintAppraisalViewModel tool = _Tool(instance);
        tool.PasteText = "Hobgoblin I Blueprint\t1\r\nSmall Armor Repairer I Blueprint\t1\r\nCivilian Data Analyzer Blueprint\t1\r\nTritanium\t100";

        await tool.AppraiseCommand.ExecuteAsync(null);

        Assert.Equal([HobgoblinBlueprint, RepairerBlueprint, AnalyzerBlueprint], tool.Rows.Select(row => row.TypeId));
        Assert.Equal([(decimal?)HobgoblinProfit, RepairerProfit, null], tool.Rows.Select(row => row.Profit));
        Assert.Equal([true, false, false], tool.Rows.Select(row => row.IsChecked));
        Assert.Equal("no price", tool.Rows[2].ProfitText);
        Assert.Contains(tool.Unresolved, line => line.StartsWith("Tritanium", StringComparison.Ordinal));
        Assert.Same(tool.Rows[0], tool.SelectedRow);
    }

    [AvaloniaFact]
    public async Task Totals_SayMaxProfit_BuildEverything_AndLossMaking()
    {
        using TestClientInstance instance = await _InstanceAsync();
        BlueprintAppraisalViewModel tool = await _AppraisedAsync(instance);

        Assert.Equal("910 ISK", tool.MaxProfitText);
        Assert.Equal($"{HobgoblinProfit + RepairerProfit:N0} ISK", tool.BuildEverythingText);
        Assert.Equal("1 blueprint · 1 without a price", tool.LossMakingText);
        Assert.Equal("1 of 3", tool.CheckedText);
    }

    [AvaloniaFact]
    public async Task ShoppingList_SumsTheCheckedBlueprintsOnly_AndCopiesAsMultibuy()
    {
        using TestClientInstance instance = await _InstanceAsync();
        RecordingDialogService dialogs = new();
        BlueprintAppraisalViewModel tool = await _AppraisedAsync(instance, dialogs);

        Assert.Equal([("Pyerite", 50L), ("Tritanium", 100L)], tool.ShoppingList.Select(line => (line.Name, line.Quantity)));

        tool.Rows[1].IsChecked = true;
        await tool.CopyShoppingListCommand.ExecuteAsync(null);

        Assert.Equal([("Pyerite", 50L), ("Tritanium", 300L)], tool.ShoppingList.Select(line => (line.Name, line.Quantity)));
        Assert.Equal($"Pyerite 50{Environment.NewLine}Tritanium 300", dialogs.LastClipboardText);

        tool.Rows[0].IsChecked = false;
        tool.Rows[1].IsChecked = false;
        Assert.Empty(tool.ShoppingList);
        Assert.False(tool.CopyShoppingListCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task RunsAndMe_ValueTheBuildAgain_AndTeOnlyMovesTheJobTime()
    {
        using TestClientInstance instance = await _InstanceAsync();
        BlueprintAppraisalViewModel tool = await _AppraisedAsync(instance);
        BlueprintAppraisalRowViewModel hobgoblin = tool.Rows[0];

        hobgoblin.Runs = 3;
        await tool.Pending;
        Assert.Equal(3 * HobgoblinProfit, hobgoblin.Profit);
        Assert.Equal([("Pyerite", 150L), ("Tritanium", 300L)], tool.ShoppingList.Select(line => (line.Name, line.Quantity)));

        hobgoblin.MaterialEfficiency = 10;
        await tool.Pending;
        Assert.Equal([135L, 270L], hobgoblin.Appraisal?.Materials.Select(material => material.Needed).Reverse());

        string before = hobgoblin.BuildTimeText;
        hobgoblin.TimeEfficiency = 20;
        Assert.Equal("1h 12m", hobgoblin.BuildTimeText);
        Assert.Equal("1h 30m", before);
    }

    [AvaloniaFact]
    public async Task RunsAreHeldWithinTheBlueprintsProductionLimit()
    {
        using TestClientInstance instance = await _InstanceAsync();
        BlueprintAppraisalViewModel tool = await _AppraisedAsync(instance);

        tool.Rows[0].Runs = 999;
        await tool.Pending;

        Assert.Equal(10, tool.Rows[0].Runs);
    }

    [AvaloniaFact]
    public async Task OpenInAppraisal_SwitchesToBlueprints_AndShowsThatBlueprint()
    {
        using TestClientInstance instance = await _InstanceAsync();
        var appraisal = new AppraisalViewModel([], _Sde(instance), blueprints: _Tool(instance));

        await appraisal.ShowBlueprintAsync(HobgoblinBlueprint);

        Assert.True(appraisal.IsBlueprintsMode);
        Assert.Equal(HobgoblinBlueprint, appraisal.Blueprints?.SelectedRow?.TypeId);
        Assert.True(appraisal.Blueprints?.SelectedRow?.IsChecked);

        await appraisal.ShowBlueprintAsync(HobgoblinBlueprint);
        Assert.Single(appraisal.Blueprints?.Rows ?? []);
    }

    [AvaloniaFact]
    public void ModeSwitch_IsOnlyOffered_WithABlueprintMode()
    {
        using TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()));
        var appraisal = new AppraisalViewModel([], _Sde(instance));

        appraisal.IsBlueprintsMode = true;

        Assert.False(appraisal.HasBlueprintMode);
        Assert.True(appraisal.IsItemsMode);
    }

    [AvaloniaFact]
    public async Task BlueprintLootLine_OffersOpenInAppraisal_ThatOpensTheToolOnIt()
    {
        using TestClientInstance instance = await _InstanceAsync();
        var dialogs = (RecordingDialogService)instance.Services.GetRequiredService<IDialogService>();
        var loot = new RunLootViewModel(instance.Services.GetRequiredService<IDispatcher>(), sde: _Sde(instance),
            appraisalOpener: instance.Services.GetRequiredService<AppraisalOpener>());

        await loot.LoadAsync(
        [
            new RunLootCaptureDto(Guid.NewGuid(), DateTime.UtcNow, IsExcluded: false, ContentHash: null, LootCaptureSource.Pasted,
                LootCaptureRole.Snapshot,
            [
                new RunLootEntryDto(HobgoblinBlueprint, "Hobgoblin I Blueprint", 1, null, LootKind.Gained, 910m, LootPriceBasis.BlueprintAppraisal),
                new RunLootEntryDto(Tritanium, "Tritanium", 100, null, LootKind.Gained, 5m)
            ])
        ], Token);

        ActivityLootLineViewModel blueprint = Assert.Single(loot.ItemRows, row => row.ItemTypeId == HobgoblinBlueprint);
        ActivityLootLineViewModel mineral = Assert.Single(loot.ItemRows, row => row.ItemTypeId == Tritanium);
        Assert.False(mineral.CanOpenInAppraisal);
        Assert.True(blueprint.CanOpenInAppraisal);

        blueprint.OpenInAppraisalCommand?.Execute(null);

        Assert.Equal(HobgoblinBlueprint, dialogs.LastAppraisalBlueprintTypeId);
        Assert.True(dialogs.LastAppraisal?.HasBlueprintMode);
    }

    private static async Task<BlueprintAppraisalViewModel> _AppraisedAsync(TestClientInstance instance,
        RecordingDialogService? dialogs = null)
    {
        BlueprintAppraisalViewModel tool = _Tool(instance, dialogs);
        tool.PasteText = "Hobgoblin I Blueprint\t1\r\nSmall Armor Repairer I Blueprint\t1\r\nCivilian Data Analyzer Blueprint\t1";
        await tool.AppraiseCommand.ExecuteAsync(null);
        return tool;
    }

    private static BlueprintAppraisalViewModel _Tool(TestClientInstance instance, RecordingDialogService? dialogs = null) =>
        new(instance.Services.CreateScope().ServiceProvider.GetRequiredService<IBlueprintAppraisalService>(), _Sde(instance), dialogs);

    private static ISdeAccessor _Sde(TestClientInstance instance) => instance.Services.GetRequiredService<ISdeAccessor>();

    private static async Task<TestClientInstance> _InstanceAsync()
    {
        TestClientInstance instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
                .Add(Tritanium, "Tritanium", 18, 4)
                .Add(Pyerite, "Pyerite", 18, 4)
                .Add(Hobgoblin, "Hobgoblin I", 100, 18)
                .Add(HobgoblinBlueprint, "Hobgoblin I Blueprint", 1146, 9)
                .Add(Repairer, "Small Armor Repairer I", 62, 7)
                .Add(RepairerBlueprint, "Small Armor Repairer I Blueprint", 1146, 9)
                .Add(Analyzer, "Civilian Data Analyzer", 538, 7)
                .Add(AnalyzerBlueprint, "Civilian Data Analyzer Blueprint", 1146, 9)
                .AddBlueprint(new SdeBlueprintManufacturing(HobgoblinBlueprint, Hobgoblin, 1, TimeSeconds: 1800, MaxProductionLimit: 10,
                    [new SdeBlueprintMaterial(Tritanium, 100), new SdeBlueprintMaterial(Pyerite, 50)]))
                .AddBlueprint(new SdeBlueprintManufacturing(RepairerBlueprint, Repairer, 1, TimeSeconds: 600, MaxProductionLimit: 100,
                    [new SdeBlueprintMaterial(Tritanium, 200)]))
                .AddBlueprint(new SdeBlueprintManufacturing(AnalyzerBlueprint, Analyzer, 1, TimeSeconds: 600, MaxProductionLimit: 100,
                    [new SdeBlueprintMaterial(Tritanium, 10)])));
            services.AddSingleton<IDialogService, RecordingDialogService>();
        });
        await instance.Services.GetRequiredService<IMarketPriceRepository>().ReplaceAllAsync(
        [
            _Price(Tritanium, 5), _Price(Pyerite, 10), _Price(Hobgoblin, 2_000), _Price(Repairer, 500)
        ], Token);
        return instance;
    }

    private static LocalMarketPrice _Price(int typeId, double price) =>
        new() { TypeId = typeId, AveragePrice = price, AdjustedPrice = price, UpdatedAt = DateTimeOffset.UtcNow };
}
