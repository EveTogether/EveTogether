using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>CONSUMABLES on the detail screen: the filament each character confirmed at SAVE, one loot line per pilot,
/// priced through the registry's own share (ET-249, ET-256, ET-329) — never a total of its own.</summary>
public sealed partial class ConsumablesDetailSectionViewModel(RunDetailSectionServices services)
    : RunDetailSection(RunSectionId.Consumables, "CONSUMABLES")
{
    public ObservableCollection<ActivityLootLineViewModel> Rows { get; } = [];

    [ObservableProperty] private bool _hasConsumables;

    [ObservableProperty] private string _costText = string.Empty;

    [ObservableProperty] private string? _emptyText;

    public override bool HasContent => HasConsumables;

    public override void Apply(RunDetailSectionInput input)
    {
        ActivityDetailDto detail = input.Detail;
        Dictionary<Guid, long> characterByRun = detail.Runs.ToDictionary(run => run.RunId, run => run.CharacterId);
        Dictionary<Guid, int> countByRun = detail.Parameters
            .Where(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilamentCount
                                 && int.TryParse(parameter.TypedValue, out _))
            .ToDictionary(parameter => parameter.RunId, parameter => int.Parse(parameter.TypedValue));

        HasConsumables = countByRun.Count > 0;
        // This section's share of TOTAL ISK as the registry counted it (ET-256), never a sum of its own — already
        // negative (a cost), so the per-row split below divides it back out rather than re-pricing anything.
        decimal? cost = detail.Isk.Of(IskSource.Consumables)?.Amount;
        int totalCount = countByRun.Values.Sum();
        decimal? unitCost = cost is { } total && totalCount > 0 ? -total / totalCount : null;

        Rows.Clear();
        foreach ((Guid runId, int count) in countByRun.Where(entry => characterByRun.ContainsKey(entry.Key)))
        {
            (int typeId, string name) = _FilamentOf(detail, runId);
            Rows.Add(new ActivityLootLineViewModel(typeId, name, count, unitCost, LootKind.Lost,
                ownerText: input.NameOf(characterByRun[runId])) { IsAlternate = Rows.Count % 2 == 1 });
        }

        CostText = cost is { } isk ? IskFormat.Whole(isk) : "no figure yet";
        EmptyText = HasConsumables ? null : "No filament count was confirmed for this activity.";
        HeaderSummary = HasConsumables ? $"{CostText} · {totalCount} filaments" : "nothing confirmed";
    }

    public override async Task LoadAsync(RunDetailSectionInput input, bool followUp, CancellationToken cancellationToken)
    {
        if (services.Images is not { } images)
            return;

        foreach (ActivityLootLineViewModel row in Rows.Where(row => row.ItemTypeId > 0))
            await row.LoadIconAsync(images);
    }

    public override string AbsentReason(string noun) => $"no CONSUMABLES — {noun} used no tracked consumable";

    /// <summary>The filament a run was saved against: the type id SAVE stored, named as the SDE names it, and the
    /// pocket's own tier and weather when the SDE has no such type — a saved run never guesses a type.</summary>
    private (int TypeId, string Name) _FilamentOf(ActivityDetailDto detail, Guid runId)
    {
        RunParameterDto[] own = [.. detail.Parameters.Where(parameter => parameter.RunId == runId)];
        int typeId = own.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilamentTypeId)
            is { } stored && int.TryParse(stored.TypedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
        string name = typeId > 0 && services.Sde?.GetType(typeId)?.Name is { } sdeName
            ? sdeName
            : $"{AbyssalFilamentName.From(own.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilament)?.TypedValue)} Filament";
        return (typeId, name);
    }
}
