using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using EveUtils.Client.Formatting;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One activity — one site, flown once — however many pilots were on it. Six saved runs under one group code are
/// this one row and not six: the row reads <c>ActivitySummary</c> through
/// <see cref="EveUtils.Shared.Modules.Runs.Queries.GetActivityOverviewQuery"/>, which already groups on
/// <c>GroupCode ?? RunId</c>. Binding to <c>Run</c> instead is what ET-161 AC-5 catches.
///
/// One line of a virtualised list, the same height at every width (ET-290): time, the type's icon, the site with
/// TYPE · system · chips under it, the crew as a stack of hexes, ISK over the duration, and where it stands towards a
/// server. What does not fit is trimmed, chips included — a row that grew would move every row under it in a list that
/// cannot measure what it has not drawn — and the activity pane (RO-2) is where all of it can be read. Nothing here is
/// looked up once the row exists: its type and system come from <see cref="RunRowFacts"/>, while the row is built off
/// the UI thread.
/// </summary>
public sealed partial class ActivityOverviewRowViewModel : ViewModelBase
{
    private const int MaxCrewFaces = 5;

    private readonly Func<ActivityOverviewRowViewModel, Task> _loadSubRuns;
    private readonly Func<ActivityOverviewRowViewModel, Task> _openDetail;
    private readonly Func<ActivityOverviewRowViewModel, Task>? _publish;
    private readonly Func<ActivityOverviewRowViewModel, Task>? _retryPublish;
    private readonly ActivityOverviewRowDto _source;
    private readonly RunPublishProgress? _publishProgress;
    private readonly string[] _otherEarnerNames;
    private readonly string _groupNetText;
    private bool _subRunsLoaded;

    /// <param name="serverNameOf">A server's name as its tab shows it; the bare address when null.</param>
    /// <param name="publishProgress">The automatic publish of this activity in flight, or the reason it last failed
    /// (ET-245).</param>
    /// <param name="facts">Answers TYPE's ET-275 archetype fallback and the system line from the static data, once per
    /// distinct question; null reads TYPE without that fallback and the system as unknown.</param>
    /// <param name="faceOf">The shared face for a crew member by id and name, so an own character's portrait is loaded
    /// once for the whole screen; null gives every member an initial.</param>
    public ActivityOverviewRowViewModel(
        ActivityOverviewRowDto row,
        Func<long, string> nameOf,
        Func<ActivityOverviewRowViewModel, Task> loadSubRuns,
        Func<ActivityOverviewRowViewModel, Task> openDetail,
        Func<ActivityOverviewRowViewModel, Task>? publish = null,
        Func<string, string>? serverNameOf = null,
        RunPublishProgress? publishProgress = null,
        Func<ActivityOverviewRowViewModel, Task>? retryPublish = null,
        RunRowFacts? facts = null,
        Func<long, string, CharacterFaceViewModel>? faceOf = null)
    {
        _loadSubRuns = loadSubRuns;
        _openDetail = openDetail;
        _publish = publish;
        _retryPublish = retryPublish;
        _source = row;
        _publishProgress = publishProgress;
        ActivitySummaryId = row.ActivitySummaryId;
        GroupCode = row.GroupCode;
        RunId = row.RunId;
        StartedAtLocal = row.StartedAtUtc.ToLocalTime();
        Duration = TimeSpan.FromSeconds(row.DurationSeconds);
        TimeText = StartedAtLocal.ToString("HH:mm");
        RunTypeDefinition type = facts?.TypeOf(row.ActivityKind, row.SignatureGroupSnapshot, row.SiteTypeId, row.SiteName)
            ?? RunTypeCatalogue.For(row.ActivityKind, row.SignatureGroupSnapshot, row.SiteTypeId, null, row.SiteName);
        // An abyssal has no site at all — it never reads "Unnamed site" (ET-241), it reads what filament opened it,
        // or the type's own honest name while that is still unknown.
        SiteText = !string.IsNullOrWhiteSpace(row.SiteName)
            ? row.SiteName
            : type.Space is RunSpace.AbyssalPocket
                ? AbyssalFilamentName.From(row.AbyssalFilamentText)
                : "Unnamed site";
        // An abyssal with no known filament falls back to the type's own name for both SiteText and KindText
        // ("Abyssal"), which would read "Abyssal Abyssal" side by side. The site name alone already says it.
        KindText = SiteText == type.Name ? string.Empty : type.Name;
        TypeText = type.Name.ToUpperInvariant();
        TypeIcon = type.Icon;

        SdeSolarSystem? system = facts?.SystemOf(row.SolarSystemId);
        HasSystem = system is not null;
        SystemText = system?.Name ?? "—";
        SecurityText = system is null ? string.Empty : RunRowFacts.SecurityText(system.SecurityStatus);
        SystemLineText = system is null ? SystemText : $"{SystemText} {SecurityText}";
        // Never "system unknown" on the row: a dash, and the why on hover — most runs started by hand simply never
        // learned where they were.
        SystemTooltip = system is not null
            ? null
            : row.SolarSystemId is { } unknownId
                ? $"Solar system {unknownId} is not in the static data yet"
                : "No solar system was recorded for this activity";

        DurationText = Duration.ToString(@"hh\:mm\:ss");
        // What this machine's own characters made of it (ET-296), out of the summary's stored per-character split —
        // never a sum of this row's own choosing: adding bounty and loot here alone left a mission's rewards out of
        // it (ET-256). The group's own total, fleet mates included, is the detail screen's to show.
        Isk = row.OwnIsk;
        IsFlownByOwnCharacter = row.IsFlownByOwnCharacter;
        NetIsk = row.OwnIsk.HasFigure ? row.OwnIsk.Total : null;
        HasNet = NetIsk.HasValue;
        NetText = NetIsk is { } net
            ? (net < 0 ? string.Empty : "+") + IskFormat.Compact(net) + " ISK" + IskFormat.ExpectedPart(row.OwnIsk)
            : string.Empty;
        // He flew it himself and recorded nothing on it, while the activity was valued all the same: every figure of
        // it sits on a fleet mate's run. Measured on his own store (11 Sep 2026): five abyssal duos whose loot his
        // mate pasted, every one of which used to read "nothing valued" — which says nobody could price it, where
        // the truth is that it was priced and the money is someone else's.
        IsRecordedOnFleetMate = row.IsFlownByOwnCharacter && !row.OwnIsk.HasFigure && row.Isk.HasFigure;
        // Only a mate whose own run recorded a name (ET-212) is named: a bare character id reads worse in a money
        // tooltip than the plain "a fleet mate's run" it falls back to.
        _otherEarnerNames = IsRecordedOnFleetMate
            ? [.. row.OtherEarners
                .Where(member => !string.IsNullOrWhiteSpace(member.CharacterNameSnapshot))
                .Select(member => CharacterNameResolver.Resolve(member.CharacterNameSnapshot, member.CharacterId, nameOf))]
            : [];
        _groupNetText = IskFormat.Compact(row.Isk.Total) + " ISK";

        // Snapshot first, then the live roster, then the bare id (ET-212, ET-247) — the exact chain the expanded
        // row already follows, so this line and that one can never name the same pilot two different ways.
        string[] crewNames = [.. row.Crew.Select(member =>
            CharacterNameResolver.Resolve(member.CharacterNameSnapshot, member.CharacterId, nameOf))];
        CrewText = row.Crew.Count == 0 ? $"{row.ParticipantCount} pilots" : string.Join(" · ", crewNames);
        HasCrewStack = row.Crew.Count > 1;
        SoloCrewText = HasCrewStack ? string.Empty : CrewText;
        CrewFaces = HasCrewStack
            ? [.. row.Crew.Take(MaxCrewFaces).Select((member, index) =>
                faceOf?.Invoke(member.CharacterId, crewNames[index]) ?? new CharacterFaceViewModel(member.CharacterId, crewNames[index]))]
            : [];
        // Five faces and the rest counted: "×4" when every pilot has a face, "+3" for the ones that do not fit.
        CrewCountText = !HasCrewStack
            ? string.Empty
            : row.Crew.Count > MaxCrewFaces ? $"+{row.Crew.Count - MaxCrewFaces}" : $"×{row.Crew.Count}";

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
        // A failure the store has since overtaken — published after all, by hand or by a later attempt — is history,
        // not a state, and must not keep saying "failed" on a row the server holds.
        HasPublishFailure = publishProgress is { Phase: RunPublishPhase.Failed } && !IsOnServer;
        PublishFailureText = HasPublishFailure ? publishProgress?.Message : null;
        SyncText = _SyncText(row.ServerSyncStates, publishProgress, serverNameOf ?? (address => address));
        (SyncIcon, IsSyncAttention, IsSyncFailed) = HasPublishFailure
            ? (MaterialIconKind.AlertCircleOutline, false, true)
            : publishProgress is { Phase: RunPublishPhase.Publishing }
                ? (MaterialIconKind.CloudUploadOutline, true, false)
                : IsQueuedForServer
                    ? (MaterialIconKind.ClockOutline, true, false)
                    : IsBehindServer
                        ? (MaterialIconKind.ArrowUpBoldCircleOutline, true, false)
                        : (MaterialIconKind.CloudCheckOutline, false, false);
        // A homefront's payout, once the site reads Completed, counts straight away and has no RunParameter row of
        // its own unless the pilot typed a different figure (ET-269: there is no wallet to confirm it against) — so
        // it is not among row.Rewards, and is read off the same Isk breakdown NetText already uses instead of a
        // second computation here. Once a pilot types a correction, HomefrontPayoutIskContributor contributes
        // nothing for that run and the FixedPayout chip from row.Rewards below carries the typed figure instead.
        IEnumerable<ActivityRewardChipViewModel> chips = row.Rewards
            .OrderBy(reward => (int)reward.ParameterKey)
            .Select(reward => new ActivityRewardChipViewModel(reward.ParameterKey, reward.Amount));
        if (row.OwnIsk.Of(IskSource.HomefrontPayout) is { } homefrontPayout)
            chips = chips.Append(new ActivityRewardChipViewModel(RunParameterKey.FixedPayout, homefrontPayout.Amount));
        Chips = [.. chips];
    }

    public Guid ActivitySummaryId { get; }

    public string? GroupCode { get; }

    public Guid? RunId { get; }

    /// <summary>The activity's own day, in the reader's zone — the day header groups on this, not on UTC.</summary>
    public DateTime StartedAtLocal { get; }

    public TimeSpan Duration { get; }

    /// <summary>This machine's own characters' share of the activity's TOTAL ISK, by source (ET-296) — what the row's
    /// figure adds up, and what its day's source bar splits (<see cref="RunsActivitySummaryText.SourcesFor"/>).</summary>
    public IskBreakdown Isk { get; }

    public decimal? NetIsk { get; }

    /// <summary>Whether any of this machine's own characters flew it — false on a row a server tab holds for a
    /// group this pilot has no run in, and on one whose character has been taken out of the registry.</summary>
    public bool IsFlownByOwnCharacter { get; }

    /// <summary>He flew it, his own run records nothing of value, and the activity was valued all the same: the
    /// proceeds are booked on a fleet mate's run (ET-296).</summary>
    public bool IsRecordedOnFleetMate { get; }

    public string TimeText { get; }
    public string SiteText { get; }
    public string KindText { get; }
    public string TypeText { get; }
    public MaterialIconKind TypeIcon { get; }
    public string DurationText { get; }
    public string CrewText { get; }
    public string EnemiesText { get; }

    public bool HasSystem { get; }
    public string SystemText { get; }
    public string SecurityText { get; }

    /// <summary>"Alkabsi 0.7", or the dash — one text, since a row is realised again every time it scrolls in.</summary>
    public string SystemLineText { get; }

    public string? SystemTooltip { get; }

    /// <summary>Several pilots flew it: the crew cell draws their faces rather than a name, and the caret unfolds the
    /// activity into one line per pilot.</summary>
    public bool HasCrewStack { get; }

    /// <summary>The one pilot's name, when there is one.</summary>
    public string SoloCrewText { get; }

    public IReadOnlyList<CharacterFaceViewModel> CrewFaces { get; }

    public string CrewCountText { get; }

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
    /// them until PUBLISH is pressed again, or a fleet run's automatic publish sends it (ET-245) — said on the row so
    /// the difference is never silent.</summary>
    public bool IsBehindServer { get; }

    /// <summary>Where the activity stands towards its server, naming the server (ET-245): with fleet runs published by
    /// themselves, "published" alone no longer says whether it went, or where.</summary>
    public string SyncText { get; }

    public bool HasSyncText => IsOnServer || IsQueuedForServer || IsBehindServer || _publishProgress is not null;

    /// <summary>The sync cell's glyph (ET-290): a cloud with a tick once published, a clock while queued or
    /// publishing, an arrow once changed since published, an alert when the automatic publish failed. Its tooltip
    /// is <see cref="SyncText"/>. An activity that never left this machine shows none.</summary>
    public MaterialIconKind SyncIcon { get; }

    /// <summary>Queued, publishing or changed since published: something is still on its way to the server.</summary>
    public bool IsSyncAttention { get; }

    public bool IsSyncFailed { get; }

    /// <summary>The last automatic publish of this activity failed, and nothing has put it on the server since. Never
    /// silent: the row says so and offers RETRY, and the runs stay queued for the next start or reconnect.</summary>
    public bool HasPublishFailure { get; }

    /// <summary>What the server or the connection said, for the RETRY button's tooltip.</summary>
    public string? PublishFailureText { get; }

    /// <summary>Until the activity pane takes them (RO-2), PUBLISH and RETRY stay reachable in the sync cell on hover or
    /// focus, so there is never a build without a manual publish. RETRY stands in PUBLISH's place while it applies.</summary>
    public bool HasSyncAction => CanPublish || HasPublishFailure;

    public MaterialIconKind SyncActionIcon => HasPublishFailure ? MaterialIconKind.Refresh : MaterialIconKind.CloudUploadOutline;

    public string SyncActionTooltip => HasPublishFailure ? RetryTooltip : "Publish this activity to a coupled server";

    /// <summary>The sync cell's one action: RETRY while an automatic publish has failed, PUBLISH otherwise.</summary>
    [RelayCommand]
    private Task SyncActionAsync() => HasPublishFailure ? RetryPublishAsync() : PublishAsync();

    public string RetryTooltip => PublishFailureText is { } reason
        ? $"Publishing failed: {reason} — try again"
        : "Publishing failed — try again";

    /// <summary>Whether this activity is filed under that server's tab: some of its runs were queued for it or went
    /// there.</summary>
    public bool IsPublishedTo(string serverAddress) =>
        _source.ServerSyncStates.Any(state => state.ServerAddress == serverAddress);

    [RelayCommand]
    private async Task RetryPublishAsync()
    {
        if (_retryPublish is not null)
            await _retryPublish(this);
    }

    private string _SyncText(IReadOnlyList<ActivityServerSyncDto> states, RunPublishProgress? progress,
        Func<string, string> serverNameOf)
    {
        if (HasPublishFailure && progress is not null)
            return $"publishing to {serverNameOf(progress.ServerAddress)} failed";
        if (progress is { Phase: RunPublishPhase.Publishing })
            return $"publishing to {serverNameOf(progress.ServerAddress)}…";
        if (IsQueuedForServer)
            return $"queued for {serverNameOf(states.First(state => state.IsPending).ServerAddress)}";
        if (IsBehindServer)
            return "changed since published";
        return states.Count > 0 ? $"published to {serverNameOf(states[0].ServerAddress)}" : string.Empty;
    }

    /// <summary>False when no server is coupled at all, which is also when the runs screen shows no server tab —
    /// the same rule the fit browser follows rather than offering an action with nowhere to go.</summary>
    public bool CanPublish => _publish is not null;

    [RelayCommand]
    private async Task PublishAsync()
    {
        if (_publish is not null)
            await _publish(this);
    }

    /// <summary>What stands where the ISK would be when nothing on the activity was valued. Never a "0 ISK": a zero
    /// here reads as a valuation that was taken and came out at nothing (ET-161 AC-4, ET-65 AC-7). Not "no loot or
    /// bounty" any more — since ET-256 ISK also counts rewards and payouts. Three different silences, told apart
    /// (ET-296): a dash where none of this pilot's own characters flew it, "on a fleet mate's run" where he flew it
    /// but every figure of it was recorded on somebody else's run, and only otherwise the plain "nothing valued".</summary>
    public string NoNetText =>
        !IsFlownByOwnCharacter ? "—"
        : IsRecordedOnFleetMate ? "on a fleet mate's run"
        : "nothing valued";

    /// <summary>The figure and, where the runs recorded a name, whose run it sits on — said on hover, since the ISK
    /// column has no room for it. Null on a plain unvalued row, which <see cref="NoNetText"/> already says in
    /// words.</summary>
    public string? NoNetTooltip =>
        !IsFlownByOwnCharacter ? "none of your characters flew this — the group total is in the detail"
        : !IsRecordedOnFleetMate ? null
        : _otherEarnerNames.Length == 0
            ? $"{_groupNetText} recorded on a fleet mate's run"
            : $"{_groupNetText} recorded on {string.Join(" and ", _otherEarnerNames)}'s "
              + (_otherEarnerNames.Length == 1 ? "run" : "runs");

    public ObservableCollection<ActivityRewardChipViewModel> Chips { get; }

    /// <summary>The activity's own runs, one per pilot — fetched on the first expand rather than for every row on
    /// screen, since a page of fifty rows would otherwise be fifty detail reads nobody asked for.</summary>
    public ObservableCollection<ActivityRunRowViewModel> SubRuns { get; } = [];

    [ObservableProperty] private bool _isExpanded;

    /// <summary>Why the sub-runs are not there, when they are not — a failed read is a state, not silence.</summary>
    [ObservableProperty] private string? _subRunsStatus;

    /// <summary><see cref="SubRunsStatus"/> as a line of its own in the list, under the unfolded row.</summary>
    public RunsListNote? SubRunsNote { get; private set; }

    /// <summary>Unfolded, folded, or its runs read again: the list around this row has to follow.</summary>
    public event Action<ActivityOverviewRowViewModel>? LayoutChanged;

    partial void OnIsExpandedChanged(bool value) => LayoutChanged?.Invoke(this);

    /// <summary>The runs a read brought back, or why it brought none.</summary>
    public void ShowSubRuns(IReadOnlyList<ActivityRunRowViewModel> runs, string? status)
    {
        SubRuns.Clear();
        foreach (ActivityRunRowViewModel run in runs)
            SubRuns.Add(run);
        SubRunsStatus = status;
        SubRunsNote = status is null ? null : new RunsListNote(status);
        LayoutChanged?.Invoke(this);
    }

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
    /// reference. Reads nothing but what the row was built from, so a refresh may ask it off the UI thread.</summary>
    public bool IsShowing(ActivityOverviewRowDto row, bool canPublish, RunPublishProgress? publishProgress = null) =>
        CanPublish == canPublish
        && _publishProgress == publishProgress
        && _WithoutLists(_source) == _WithoutLists(row)
        && _source.Crew.SequenceEqual(row.Crew)
        && _source.Rewards.SequenceEqual(row.Rewards)
        && _source.ServerSyncStates.SequenceEqual(row.ServerSyncStates)
        && _source.OtherEarners.SequenceEqual(row.OtherEarners);

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
        Crew = Array.Empty<ActivityCrewMemberDto>(),
        Rewards = Array.Empty<ActivityRewardDto>(),
        ServerSyncStates = Array.Empty<ActivityServerSyncDto>(),
        // A list compares by reference on a record, and every read builds a new one — left in, no row would ever
        // be held onto across a refresh (ET-222), and the whole list would be rebuilt on every tick (ET-287).
        OtherEarners = Array.Empty<ActivityCrewMemberDto>()
    };

    /// <summary>TYPE, from the same catalogue every other run list reads (ET-226) — "Data Site", "Mission run", …,
    /// never a bare kind name. Shared with <see cref="UnfinishedRunViewModel"/>, the only other reader of a run's
    /// type outside this row itself.
    ///
    /// Goes through <see cref="RunTypeCatalogue"/>'s own three-argument <c>For</c> directly rather than
    /// <see cref="RunTypeResolver"/> plus the bare <c>For(RunTypeId)</c> this used to chain (ET-268): that pair skips
    /// the per-run homefront refinement <c>For(kind, group, siteTypeId)</c> does, so a homefront read back plain
    /// "Homefront" here even where <paramref name="siteTypeId"/> was known and every other screen already said
    /// "Homefront · Raid".</summary>
    public static string KindLabel(ActivityKind kind, string? signatureGroupSnapshot, int siteTypeId = 0,
        ISdeAccessor? sde = null, string? siteName = null) =>
        RunTypeCatalogue.For(kind, signatureGroupSnapshot, siteTypeId, sde, siteName).Name;
}
