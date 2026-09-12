using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
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

    // What the other pilots on this run share of their own loot (ET-242), and which share of each is on screen now.
    private readonly FleetRunShares? _fleetShares;
    private readonly Dictionary<long, long> _sharedShownAt = [];
    private bool _isSyncingShared;
    private bool _isSharedSyncOwed;

    public LootWindowSectionViewModel(IRunWindowContext context) : base(context, RunSectionId.Loot, "LOOT")
    {
        LootModeChoices = LootModes
            .Select((mode, index) => new ActivityChoice { Index = index, Label = _LabelOf(mode) })
            .ToList();

        _fleetShares = context.Services.GetService<FleetRunShares>();
        if (_fleetShares is not null)
            _fleetShares.Changed += _OnFleetShareChanged;
        // A group mate's run read from the store takes over from their live share, so the blocks are looked at again
        // whenever the group's own blocks change.
        if (LootOverview is not null)
            LootOverview.Characters.CollectionChanged += _OnOwnBlocksChanged;
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
        // A window reopened mid-run shows what the others shared before it existed.
        _ = _SyncSharedAsync();
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

    protected override void OnContextChanged(string? propertyName)
    {
        if (propertyName is nameof(IRunWindowContext.GroupCode) or nameof(IRunWindowContext.FleetId))
            _ = _SyncSharedAsync();
    }

    public override void Dispose()
    {
        if (_fleetShares is not null)
            _fleetShares.Changed -= _OnFleetShareChanged;
        if (LootOverview is not null)
            LootOverview.Characters.CollectionChanged -= _OnOwnBlocksChanged;
        base.Dispose();
    }

    private void _OnFleetShareChanged(string groupCode) => Dispatcher.UIThread.Post(() =>
    {
        if (groupCode == Context.GroupCode)
            _ = _SyncSharedAsync();
    });

    private void _OnOwnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e) => _ = _SyncSharedAsync();

    // One pass at a time, the newest shares winning: a block is loaded asynchronously, and two passes over the same one
    // could land in the wrong order. A change that arrives mid-pass gets a pass of its own after it.
    private async Task _SyncSharedAsync()
    {
        if (_isSyncingShared)
        {
            _isSharedSyncOwed = true;
            return;
        }

        _isSyncingShared = true;
        try
        {
            do
            {
                _isSharedSyncOwed = false;
                await _ShowSharedAsync();
            }
            while (_isSharedSyncOwed);
        }
        finally
        {
            _isSyncingShared = false;
        }
    }

    /// <summary>
    /// The others' live shares of this run, one block each under the group's own (ET-242). Never this client's own
    /// characters — a second toon of this pilot's in the same fleet hears the first one's share come back over its own
    /// connection, and ET-210's toons are this pilot's own blocks already — and never a pilot whose run the group
    /// already reads from the store: that block is the same loot, and the one that stays.
    /// </summary>
    private async Task _ShowSharedAsync()
    {
        if (LootOverview is not { } overview)
            return;

        List<(int CharacterId, RunShareUpdate Share)> shares = Context.GroupCode is { } groupCode
                                                               && Context.FleetId is { } fleetId && _fleetShares is not null
            ? [.. _fleetShares.Of(groupCode).Where(entry => entry.Share.FleetId == fleetId && entry.Share.SharesLoot)]
            : [];
        if (shares.Count > 0)
        {
            HashSet<long> own = [.. overview.Characters.Select(block => block.CharacterId)];
            if (Context.Services.GetService<ICharacterRegistry>() is { } registry)
                own.UnionWith((await registry.GetAllAsync())
                    .Select(character => character.EsiCharacterId).OfType<int>().Select(characterId => (long)characterId));
            shares.RemoveAll(entry => own.Contains(entry.CharacterId));
        }

        overview.KeepShared([.. shares.Select(entry => (long)entry.CharacterId)]);
        foreach (long gone in _sharedShownAt.Keys.Where(characterId => shares.All(entry => entry.CharacterId != characterId)).ToList())
            _sharedShownAt.Remove(gone);

        foreach ((int characterId, RunShareUpdate share) in shares)
        {
            if (_sharedShownAt.GetValueOrDefault(characterId) == share.UnixMs)
                continue;

            _sharedShownAt[characterId] = share.UnixMs;
            string name = overview.FleetCharacters.FirstOrDefault(block => block.CharacterId == characterId)?.CharacterText
                          ?? await _NameOfAsync(characterId);
            await overview.ShowSharedAsync(characterId, name, [.. share.Loot.Select(_EntryOf)], share.CaptureCount,
                DateTimeOffset.FromUnixTimeMilliseconds(share.UnixMs).UtcDateTime);
        }
    }

    // The share names no item: this client's own SDE does, the same one every other loot line is read against.
    private RunLootEntryDto _EntryOf(RunShareLootLine line) =>
        new(line.TypeId,
            Context.Services.GetService<ISdeAccessor>() is { } sde && sde.TryGetTypeName(line.TypeId, out string name)
                ? name
                : $"type {line.TypeId}",
            line.Quantity, ClipboardPrice: null, line.Kind);

    /// <summary>The name the FLEET section already resolved for this pilot, or public ESI's; the id when neither can
    /// say.</summary>
    private async Task<string> _NameOfAsync(int characterId)
    {
        if (Context.FleetMembers.FirstOrDefault(member => member.CharacterId == characterId) is { } member
            && member.Name != $"Char {characterId}")
            return member.Name;

        if (Context.Services.GetService<IExternalCharacterLookup>() is { } lookup
            && await lookup.LookupAsync(characterId) is { Exists: true } info)
            return info.Name;

        return $"Char {characterId}";
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
