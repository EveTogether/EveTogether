using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>MINING on the detail screen: what the gamelog recorded, per character and per ore, with the total apart
/// (ET-229) — general for any mining, not only a homefront's.</summary>
public sealed partial class MiningDetailSectionViewModel(RunDetailSectionServices services)
    : RunDetailSection(RunSectionId.Mining, "MINING")
{
    public ObservableCollection<ActivityMiningRowViewModel> Rows { get; } = [];

    [ObservableProperty] private bool _hasMining;

    [ObservableProperty] private string _miningText = string.Empty;

    [ObservableProperty] private string? _emptyText;

    public override bool HasContent => HasMining;

    public override void Apply(RunDetailSectionInput input)
    {
        ActivityDetailDto detail = input.Detail;
        HasMining = detail.MiningEntries.Count > 0;
        // This section's share of TOTAL ISK as the registry counted it (ET-256), not a sum of its own.
        MiningText = IskFormat.WholeOrNoPrice(
            detail.Isk.Of(IskSource.Mining) is { Certainty: not IskCertainty.Unknown } mining ? mining.Amount : null);
        EmptyText = HasMining ? null : "No mining line came past in the game log for this activity.";
        HeaderSummary = HasMining ? $"{MiningText} · {detail.MiningEntries.Count} ore lines" : "nothing measured";
    }

    /// <summary>Prices every ore by exact SDE name, one appraisal call for the whole activity's distinct types
    /// (Mutanite never needs it — <see cref="MiningValuation"/>). Skipped when the activity has no mining at all, or
    /// when the SDE is unavailable.</summary>
    public override async Task LoadAsync(RunDetailSectionInput input, bool followUp, CancellationToken cancellationToken)
    {
        if (services.Sde is not { IsAvailable: true } sde || input.Detail.MiningEntries.Count == 0)
        {
            Rows.Clear();
            return;
        }

        Dictionary<Guid, long> characterByRun = input.Detail.Runs.ToDictionary(run => run.RunId, run => run.CharacterId);
        List<(RunMiningEntryDto Entry, int? TypeId)> resolved = [.. input.Detail.MiningEntries
            .Where(entry => characterByRun.ContainsKey(entry.RunId))
            .Select(entry => (entry, sde.TryGetTypeId(entry.OreType, out int id) ? (int?)id : null))];

        Dictionary<int, double> prices = new();
        int[] typeIds = [.. resolved.Select(r => r.TypeId).OfType<int>().Distinct()];
        if (typeIds.Length > 0 && services.Appraisal is { } appraisal)
        {
            Result<AppraisalOutcome> valued = await appraisal.AppraiseAsync(
                [.. typeIds.Select(id => new AppraisalLine(id, string.Empty, 1))], cancellationToken);
            if (valued.IsSuccess)
                foreach (AppraisalRow row in valued.Value!.Rows)
                    if (row.Price?.Estimate is { } estimate)
                        prices[row.Line.TypeId] = estimate;
        }

        Rows.Clear();
        foreach ((RunMiningEntryDto entry, int? typeId) in resolved.OrderByDescending(r => r.Entry.Units))
        {
            decimal? unitPrice = typeId is { } id ? MiningValuation.UnitPrice(sde, id, prices) : null;
            bool isFixedPrice = typeId is { } fixedId && sde.GetType(fixedId)?.GroupId == MiningValuation.MutaniteGroupId;
            Rows.Add(new ActivityMiningRowViewModel(
                entry.RunId, characterByRun[entry.RunId], entry.OreType, entry.Units, entry.CriticalUnits,
                entry.ResidueUnits, unitPrice is { } price ? price * entry.Units : null, isFixedPrice, input.NameOf));
        }
    }

    public override string AbsentReason(string noun) => $"no MINING — {noun} has nothing mined in your game log";
}
