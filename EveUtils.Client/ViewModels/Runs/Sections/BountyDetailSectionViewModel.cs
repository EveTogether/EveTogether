using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>BOUNTY on the detail screen: what the gamelog paid out, per character, with the total apart.</summary>
public sealed partial class BountyDetailSectionViewModel() : RunDetailSection(RunSectionId.Bounty, "BOUNTY")
{
    public ObservableCollection<ActivityBountyRowViewModel> BountyRows { get; } = [];

    [ObservableProperty] private bool _hasBountyFigures;

    [ObservableProperty] private string _bountyText = string.Empty;

    [ObservableProperty] private string? _bountyEmptyText;

    public override bool HasContent => HasBountyFigures;

    public override void Apply(RunDetailSectionInput input)
    {
        ActivityDetailDto detail = input.Detail;
        // Not "0 ISK": BountyIsk is zero both when nothing was shot and when nothing was measured, and only the
        // absence of bounty rows tells those apart.
        HasBountyFigures = detail.BountyEntries.Count > 0;
        BountyText = IskFormat.Whole(detail.BountyIsk);
        BountyEmptyText = HasBountyFigures
            ? null
            : "No bounty line came past in the game log for this activity.";
        HeaderSummary = HasBountyFigures
            ? $"{BountyText} · {detail.BountyEntries.Count} payouts"
            : "nothing measured";

        // One row per character, the same breakdown FLEET already gives for participation (ET-210 review finding,
        // 2026-09-09) — largest share first, so the reader sees who brought in the most without having to scan every
        // row.
        BountyRows.Clear();
        Dictionary<Guid, long> characterByRun = detail.Runs.ToDictionary(run => run.RunId, run => run.CharacterId);
        foreach (IGrouping<long, RunBountyEntryDto> group in detail.BountyEntries
                     .Where(entry => characterByRun.ContainsKey(entry.RunId))
                     .GroupBy(entry => characterByRun[entry.RunId])
                     .OrderByDescending(group => group.Sum(entry => entry.Isk)))
            BountyRows.Add(new ActivityBountyRowViewModel(group.Key, group.Sum(entry => entry.Isk), input.NameOf));
    }

    public override string AbsentReason(string noun) => $"no BOUNTY — {noun} has no rats whose bounty lands in your wallet";
}
