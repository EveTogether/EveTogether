using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Dtos;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// LOOT in the run window: the way this run registers loot, the two holds of the cargo way, and one block per
/// character in the group — the same component the saved activity shows (ET-215). The loot itself is the window's
/// (<see cref="IRunWindowContext.RunLoot"/>, <see cref="IRunWindowContext.LootOverview"/>): TOTAL ISK and the fleet's
/// share read it too, so this section shows it and does not own it.
/// </summary>
public sealed partial class LootWindowSectionViewModel : RunWindowSection
{
    /// <summary>Clipboard first, and first for good: it is the default and the one nobody has to choose.</summary>
    public static IReadOnlyList<ActivityLootMode> LootModes { get; } =
        [ActivityLootMode.Clipboard, ActivityLootMode.CargoDiff];

    /// <summary>Which way this kind registers loot, remembered per kind: in the abyss filaments and ammunition go up
    /// and a starting cargo hold earns its keep, on a combat site you only ever pick things up. Absent means
    /// <see cref="ActivityLootMode.Clipboard"/>, which keeps a pilot who never opens this row on exactly the way he
    /// has now.</summary>
    public static string LootModeSettingKey(ActivityKind kind) =>
        $"ui.activity.lootmode.{kind.ToString().ToLowerInvariant()}";

    public LootWindowSectionViewModel(IRunWindowContext context) : base(context, RunSectionId.Loot, "LOOT")
    {
        LootModeChoices = LootModes
            .Select((mode, index) => new ActivityChoice { Index = index, Label = _LabelOf(mode) })
            .ToList();
    }

    public RunLootViewModel? RunLoot => Context.RunLoot;

    public ActivityLootViewModel? LootOverview => Context.LootOverview;

    /// <summary>Which way this window's LOOT section is worked. The controls of the way you do not use are not on
    /// screen; the figures are not this row's business either way (Zyra, 2026-09-04).</summary>
    [ObservableProperty] private ActivityLootMode _lootMode;

    public IReadOnlyList<ActivityChoice> LootModeChoices { get; }

    /// <summary>
    /// The caption over the ISK figures. It names its own source on purpose, and it used to name the wrong one: it read
    /// "Prices are the clipboard column as it stood at the copy" long after the ISK in a copied line stopped being held
    /// at all. Which cache snapshot valued them is <see cref="RunLootViewModel.TotalIskLabel"/>'s to say, beside the
    /// total rather than twice.
    /// </summary>
    public string IskLabel => "Prices come from EVE Together's own hourly price lookup on type id.";

    public override void Load(IReadOnlyList<SettingDto>? settings)
    {
        // Anything unreadable reads as clipboard, which is the only default that cannot surprise anyone.
        if (settings is not null)
            LootMode = settings.FirstOrDefault(s => s.Key == LootModeSettingKey(Context.Kind))?.Value == nameof(ActivityLootMode.CargoDiff)
                ? ActivityLootMode.CargoDiff
                : ActivityLootMode.Clipboard;
        _SyncChoices();
    }

    // What was collected first, the value as an aside. A shut section that only reported "no price" read as a fault
    // while two items sat in it (Raymond, 2026-09-02). The whole group's, since the section under it is.
    public override void RefreshSummary() =>
        HeaderSummary = LootOverview is { HasCaptures: true } overview
            ? $"{_LootItemCount()} · {overview.NetIskDisplay}"
            : RunLoot?.RunStatusMessage ?? "no loot captured";

    [RelayCommand]
    private async Task SelectLootModeAsync(int index)
    {
        LootMode = LootModes[index];
        using var scope = Context.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
            .Send(new SetSettingCommand(LootModeSettingKey(Context.Kind), LootMode.ToString()));
    }

    /// <summary>The mode moves controls on and off screen and nothing else — the figures follow the roles on the
    /// captures, so switching back and forth cannot change what the run is worth.</summary>
    partial void OnLootModeChanged(ActivityLootMode value)
    {
        if (RunLoot is not null)
            RunLoot.IsCargoDiffShown = value is ActivityLootMode.CargoDiff;
        _SyncChoices();
    }

    private void _SyncChoices()
    {
        foreach (ActivityChoice choice in LootModeChoices)
            choice.IsSelected = LootModes[choice.Index] == LootMode;
    }

    private string _LootItemCount()
    {
        int items = LootOverview?.Characters.Sum(block => block.Loot.Captures
            .Where(capture => !capture.IsExcluded).Sum(capture => capture.Entries.Count)) ?? 0;
        return items == 1 ? "1 item" : $"{items} items";
    }

    private static string _LabelOf(ActivityLootMode mode) =>
        mode is ActivityLootMode.CargoDiff ? "start + end hold" : "clipboard";
}
