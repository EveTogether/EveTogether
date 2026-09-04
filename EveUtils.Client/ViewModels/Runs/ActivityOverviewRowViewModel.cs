using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One activity — one site, flown once — however many pilots were on it. Six saved runs under one group code are
/// this one row and not six: the row reads <c>ActivitySummary</c> through
/// <see cref="EveUtils.Shared.Modules.Runs.Queries.GetActivityOverviewQuery"/>, which already groups on
/// <c>GroupCode ?? RunId</c>. Binding to <c>Run</c> instead is what ET-161 AC-5 catches.
///
/// The shape is the same at every width, because <c>ModuleHostService</c> moves this very content between a docked
/// tab (758px) and a floating window and there is no second layout to fall back on. Three lines: the figures worth
/// scanning down a column, then what explains them, then the rewards. Nothing is folded behind a "⋯" — a reward
/// that vanishes at 758px is precisely what AC-3 forbids, and a wrapping chip strip costs a line instead of a fact.
/// </summary>
public sealed partial class ActivityOverviewRowViewModel : ViewModelBase
{
    private readonly Func<ActivityOverviewRowViewModel, Task> _loadSubRuns;
    private readonly Func<ActivityOverviewRowViewModel, Task> _openDetail;
    private bool _subRunsLoaded;

    public ActivityOverviewRowViewModel(
        ActivityOverviewRowDto row,
        Func<long, string> nameOf,
        Func<ActivityOverviewRowViewModel, Task> loadSubRuns,
        Func<ActivityOverviewRowViewModel, Task> openDetail)
    {
        _loadSubRuns = loadSubRuns;
        _openDetail = openDetail;
        ActivitySummaryId = row.ActivitySummaryId;
        StartedAtLocal = row.StartedAtUtc.ToLocalTime();
        Duration = TimeSpan.FromSeconds(row.DurationSeconds);
        TimeText = StartedAtLocal.ToString("HH:mm");
        SiteText = string.IsNullOrWhiteSpace(row.SiteName) ? "Unnamed site" : row.SiteName;
        KindText = _KindText(row.ActivityKind);
        DurationText = Duration.ToString(@"hh\:mm\:ss");
        // "Net" is what the activity brought in, which on a combat site is mostly bounty: leaving it out read a
        // 1.26M ISK evening as its 6.8k of salvage (acceptatie 2026-09-04). Null only when neither half exists —
        // a bounty of zero is "no payout came past", which is a figure, so it counts as known.
        NetIsk = row.LootIskNet is null && row.BountyIsk == 0 ? null : (row.LootIskNet ?? 0) + row.BountyIsk;
        HasNet = NetIsk.HasValue;
        NetText = NetIsk is { } net
            ? (net < 0 ? string.Empty : "+") + ActivityRewardChipViewModel.Compact(net) + " ISK"
            : string.Empty;
        CrewText = row.CharacterIds.Count == 0
            ? $"{row.ParticipantCount} pilots"
            : string.Join(" · ", row.CharacterIds.Select(nameOf));
        EnemiesText = row.EnemyTypeCount > 0
            ? $"{row.EnemyTypeCount} enemy types"
            // "Counted", not "recorded": only hand-counted enemies are stored, so a zero here is nobody typing a
            // number, never an activity without a fight.
            : "no enemies counted";
        Chips = [.. row.Rewards
            .OrderBy(reward => (int)reward.ParameterKey)
            .Select(reward => new ActivityRewardChipViewModel(reward.ParameterKey, reward.Amount))];
    }

    public Guid ActivitySummaryId { get; }

    /// <summary>The activity's own day, in the reader's zone — the day band groups on this, not on UTC.</summary>
    public DateTime StartedAtLocal { get; }

    public TimeSpan Duration { get; }

    public decimal? NetIsk { get; }

    public string TimeText { get; }
    public string SiteText { get; }
    public string KindText { get; }
    public string DurationText { get; }
    public string CrewText { get; }
    public string EnemiesText { get; }

    public bool HasNet { get; }
    public string NetText { get; }

    /// <summary>What stands where the net would be when neither a loot capture nor a bounty line was ever taken.
    /// Never a "0 ISK": a zero here reads as a valuation that was taken and came out at nothing (ET-161 AC-4,
    /// ET-65 AC-7).</summary>
    public string NoNetText => "no loot or bounty recorded";

    public ObservableCollection<ActivityRewardChipViewModel> Chips { get; }

    /// <summary>The activity's own runs, one per pilot — fetched on the first expand rather than for every row on
    /// screen, since a page of fifty rows would otherwise be fifty detail reads nobody asked for.</summary>
    public ObservableCollection<ActivityRunRowViewModel> SubRuns { get; } = [];

    [ObservableProperty] private bool _isExpanded;

    /// <summary>Why the sub-runs are not there, when they are not — a failed read is a state, not silence.</summary>
    [ObservableProperty] private string? _subRunsStatus;

    [RelayCommand]
    private async Task ToggleAsync()
    {
        IsExpanded = !IsExpanded;
        if (!IsExpanded || _subRunsLoaded)
            return;

        _subRunsLoaded = true;
        await _loadSubRuns(this);
        // A read that failed may be tried again by folding and unfolding; only a read that answered is kept.
        if (SubRunsStatus is not null)
            _subRunsLoaded = false;
    }

    [RelayCommand]
    private Task OpenDetailAsync() => _openDetail(this);

    // A kind added later gets its own name rather than an exception: this row is a list entry, and the whole screen
    // going down over one unknown value is the failure mode AGENTS.md §2 is about.
    private static string _KindText(ActivityKind kind) => kind switch
    {
        ActivityKind.Abyssal => "ABYSSAL",
        ActivityKind.Site => "SITE",
        ActivityKind.Mission => "MISSION",
        _ => kind.ToString().ToUpperInvariant()
    };
}
