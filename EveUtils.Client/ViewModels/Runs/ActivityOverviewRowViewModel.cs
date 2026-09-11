using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using EveUtils.Client.Formatting;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs;
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
    private readonly Func<ActivityOverviewRowViewModel, Task>? _publish;
    private readonly ActivityOverviewRowDto _source;
    private bool _subRunsLoaded;

    public ActivityOverviewRowViewModel(
        ActivityOverviewRowDto row,
        Func<long, string> nameOf,
        Func<ActivityOverviewRowViewModel, Task> loadSubRuns,
        Func<ActivityOverviewRowViewModel, Task> openDetail,
        Func<ActivityOverviewRowViewModel, Task>? publish = null)
    {
        _loadSubRuns = loadSubRuns;
        _openDetail = openDetail;
        _publish = publish;
        _source = row;
        ActivitySummaryId = row.ActivitySummaryId;
        StartedAtLocal = row.StartedAtUtc.ToLocalTime();
        Duration = TimeSpan.FromSeconds(row.DurationSeconds);
        TimeText = StartedAtLocal.ToString("HH:mm");
        RunTypeDefinition type = RunTypeCatalogue.For(RunTypeResolver.Resolve(row.ActivityKind, row.SignatureGroupSnapshot));
        // An abyssal has no site at all — it never reads "Unnamed site" (ET-241), it reads what filament opened it,
        // or the type's own honest name while that is still unknown.
        SiteText = !string.IsNullOrWhiteSpace(row.SiteName)
            ? row.SiteName
            : type.Space is RunSpace.AbyssalPocket
                ? AbyssalFilamentName.From(row.AbyssalFilamentText)
                : "Unnamed site";
        KindText = type.Name;
        TypeIcon = type.Icon;
        DurationText = Duration.ToString(@"hh\:mm\:ss");
        // "Net" is what the activity brought in, which on a combat site is mostly bounty: leaving it out read a
        // 1.26M ISK evening as its 6.8k of salvage (acceptatie 2026-09-04). Null only when neither half exists —
        // a bounty of zero is "no payout came past", which is a figure, so it counts as known.
        NetIsk = row.LootIskNet is null && row.BountyIsk == 0 ? null : (row.LootIskNet ?? 0) + row.BountyIsk;
        HasNet = NetIsk.HasValue;
        NetText = NetIsk is { } net
            ? (net < 0 ? string.Empty : "+") + IskFormat.Compact(net) + " ISK"
            : string.Empty;
        CrewText = row.CharacterIds.Count == 0
            ? $"{row.ParticipantCount} pilots"
            : string.Join(" · ", row.CharacterIds.Select(nameOf));
        EnemiesText = row.EnemyTypeCount > 0
            ? $"{row.EnemyTypeCount} enemy types"
            // "Counted", not "recorded": only hand-counted enemies are stored, so a zero here is nobody typing a
            // number, never an activity without a fight.
            : "no enemies counted";
        HasAutoSavedRun = row.HasAutoSavedRun;
        // Unlike a fit, which keeps no trace of having been shared, a run records where it stands towards a server —
        // so the row says it rather than making the reader open the server tab to find out.
        IsQueuedForServer = row.ServerSyncStates.Any(state => state.IsPending);
        IsBehindServer = !IsQueuedForServer && row.ServerSyncStates.Any(state => state.IsOutdated);
        IsOnServer = row.ServerSyncStates.Count > 0 && !IsQueuedForServer && !IsBehindServer;
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
    public MaterialIconKind TypeIcon { get; }
    public string DurationText { get; }
    public string CrewText { get; }
    public string EnemiesText { get; }

    public bool HasNet { get; }
    public string NetText { get; }

    /// <summary>Nobody finished this one — the app committed it a day after it was stopped (ET-179). Said on the row
    /// rather than left to the reader, who would otherwise find an activity in their history they never saved.</summary>
    public bool HasAutoSavedRun { get; }

    public string AutoSavedText => "auto-saved";

    /// <summary>On a server, and unchanged since it went there.</summary>
    public bool IsOnServer { get; }

    /// <summary>Queued for a server but not yet accepted by it — either never pushed, or edited after it was.</summary>
    public bool IsQueuedForServer { get; }

    /// <summary>Published, then its loot corrected here (ET-215). The server still holds the older figures and keeps
    /// them until PUBLISH is pressed again — said on the row so the difference is never silent.</summary>
    public bool IsBehindServer { get; }

    public string SyncText => IsQueuedForServer ? "queued" : IsBehindServer ? "changed since published" : "published";

    public bool HasSyncText => IsOnServer || IsQueuedForServer || IsBehindServer;

    /// <summary>False when no server is coupled at all, which is also when the runs screen shows no server tab —
    /// the same rule the fit browser follows rather than offering an action with nowhere to go.</summary>
    public bool CanPublish => _publish is not null;

    [RelayCommand]
    private async Task PublishAsync()
    {
        if (_publish is not null)
            await _publish(this);
    }

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
        if (IsExpanded)
            await _LoadSubRunsOnceAsync();
    }

    private async Task _LoadSubRunsOnceAsync()
    {
        if (_subRunsLoaded)
            return;

        _subRunsLoaded = true;
        await _loadSubRuns(this);
        // A read that failed may be tried again by folding and unfolding; only a read that answered is kept.
        if (SubRunsStatus is not null)
            _subRunsLoaded = false;
    }

    [RelayCommand]
    private Task OpenDetailAsync() => _openDetail(this);

    /// <summary>Whether this row already says everything <paramref name="row"/> would, so a refresh can keep this very
    /// instance on screen (ET-222). Record equality for every figure — a column added to the DTO later takes part
    /// without anyone having to list it here — and the three lists compared by content, which a record compares by
    /// reference.</summary>
    public bool IsShowing(ActivityOverviewRowDto row, bool canPublish) =>
        CanPublish == canPublish
        && _WithoutLists(_source) == _WithoutLists(row)
        && _source.CharacterIds.SequenceEqual(row.CharacterIds)
        && _source.Rewards.SequenceEqual(row.Rewards)
        && _source.ServerSyncStates.SequenceEqual(row.ServerSyncStates);

    /// <summary>Takes over from the row this one replaces on a refresh: open stays open, with its runs read again
    /// rather than left showing the figures that made the old row out of date.</summary>
    public async Task ContinueFromAsync(ActivityOverviewRowViewModel previous)
    {
        if (!previous.IsExpanded)
            return;

        IsExpanded = true;
        await _LoadSubRunsOnceAsync();
    }

    private static ActivityOverviewRowDto _WithoutLists(ActivityOverviewRowDto row) => row with
    {
        CharacterIds = Array.Empty<long>(),
        Rewards = Array.Empty<ActivityRewardDto>(),
        ServerSyncStates = Array.Empty<ActivityServerSyncDto>()
    };

    /// <summary>TYPE, from the same catalogue every other run list reads (ET-226) — "Data Site", "Mission run", …,
    /// never a bare kind name. Shared with <see cref="UnfinishedRunViewModel"/>, the only other reader of a run's
    /// type outside this row itself.</summary>
    public static string KindLabel(ActivityKind kind, string? signatureGroupSnapshot) =>
        RunTypeCatalogue.For(RunTypeResolver.Resolve(kind, signatureGroupSnapshot)).Name;
}
