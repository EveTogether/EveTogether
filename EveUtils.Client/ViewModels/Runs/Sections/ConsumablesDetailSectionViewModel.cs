using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>CONSUMABLES on the detail screen: the filament count each character confirmed at SAVE, priced through
/// the registry's own share (ET-249, ET-256) — never a total of its own.</summary>
public sealed partial class ConsumablesDetailSectionViewModel() : RunDetailSection(RunSectionId.Consumables, "CONSUMABLES")
{
    public ObservableCollection<ActivityConsumableRowViewModel> Rows { get; } = [];

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
            Rows.Add(new ActivityConsumableRowViewModel(
                input.NameOf(characterByRun[runId]), count, unitCost is { } price ? count * price : null));

        CostText = cost is { } isk ? IskFormat.Whole(isk) : "no figure yet";
        EmptyText = HasConsumables ? null : "No filament count was confirmed for this activity.";
        HeaderSummary = HasConsumables ? $"{CostText} · {totalCount} filaments" : "nothing confirmed";
    }

    public override string AbsentReason(string noun) => $"no CONSUMABLES — {noun} used no tracked consumable";
}
