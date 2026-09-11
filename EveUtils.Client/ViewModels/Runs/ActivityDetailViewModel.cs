using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Client.Formatting;
using EveUtils.Client.Imaging;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using ActivityKind = EveUtils.Shared.Modules.Runs.Enums.ActivityKind;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// ET-162: one saved activity, fully expanded — the unfolded brother of the activity window, reusing its
/// <see cref="ActivitySection"/> shape rather than putting a second look beside it.
///
/// The screen's own subject is which sections an activity kind has, and what a missing one says. Two rules, and
/// they are different on purpose:
/// <list type="bullet">
/// <item>A section the kind claims is always drawn. Empty, it carries one line naming what was not measured —
/// never a "0", which reads as a measurement that was taken and came out at nothing.</item>
/// <item>A section the kind does not claim is not drawn, and is named in <see cref="AbsentSectionsText"/> with the
/// reason. Dropping it silently would leave the reader unable to tell "there was nothing" from "this kind has no
/// such thing".</item>
/// </list>
/// A kind that does not claim a section still gets it when there is data for it, so a reward booked against a site
/// run cannot disappear behind the table.
/// </summary>
public sealed partial class ActivityDetailViewModel : ViewModelBase, IRefreshableModule, IDisposable
{
    private readonly CqrsDispatcher _dispatcher;
    private readonly IDisposable? _runChangesSubscription;
    /// <summary>Not readonly: a full delete removes the <c>ActivitySummary</c> row outright, and undoing it builds a
    /// new one. Since ET-222 that row takes the same id back, but an activity whose row was first built before then
    /// does not — <see cref="UndoDeleteAsync"/> and <see cref="_OnRunsChangedAsync"/> look it up by its runs and move
    /// this screen onto it rather than going on asking the query for an id that no longer exists (ET-214).</summary>
    private Guid _activitySummaryId;
    private readonly Func<long, string>? _nameOf;
    private readonly IEsiClient? _esi;
    private readonly IEsiLocationClient? _locations;
    private readonly ISdeAccessor? _sde;
    private readonly IReadOnlySet<long>? _ownCharacterIds;
    private readonly Func<Task>? _republish;
    private readonly IDialogService? _dialogs;

    /// <summary>The activity as it was last read — kept for <see cref="DeleteAsync"/>, which needs the group code,
    /// which of its runs are this machine's own, and the figures the confirmation names, without a second read.</summary>
    private ActivityDetailDto? _lastDetail;

    /// <summary>What <see cref="UndoDeleteAsync"/> targets — set only once <see cref="IsDeleted"/> is true.</summary>
    private string? _deletedGroupCode;
    private Guid? _deletedRunId;

    /// <param name="ownCharacterIds">This machine's own characters. A run of anyone else came in from a server and is
    /// shown read-only (ET-215); null treats every run as this machine's own.</param>
    /// <param name="republish">Publishes this activity again — the runs screen's own PUBLISH, handed down so a
    /// correction that left the server's copy behind can be answered right where it was made.</param>
    /// <param name="dialogs">Confirms the delete (ET-214). Null makes the delete control a no-op rather than skip
    /// confirmation — the same "no service, no action" rule every other optional dependency here already follows.</param>
    /// <param name="runChanges">Keeps this screen current with what happens to its activity anywhere else (ET-222).
    /// Null leaves it showing what it read, as it did before.</param>
    public ActivityDetailViewModel(CqrsDispatcher dispatcher, Guid activitySummaryId,
        IAppraisalProvider? appraisal = null, Func<long, string>? nameOf = null,
        IEsiClient? esi = null, IEsiLocationClient? locations = null, ISdeAccessor? sde = null,
        ICharacterPortraitProvider? portraits = null, ITypeImageProvider? images = null,
        IReadOnlySet<long>? ownCharacterIds = null, Func<Task>? republish = null, IDialogService? dialogs = null,
        RunChangeFeed? runChanges = null)
    {
        _dispatcher = dispatcher;
        _activitySummaryId = activitySummaryId;
        _nameOf = nameOf;
        _esi = esi;
        _locations = locations;
        _sde = sde;
        _ownCharacterIds = ownCharacterIds;
        _republish = republish;
        _dialogs = dialogs;
        LootOverview = new ActivityLootViewModel(() => new RunLootViewModel(dispatcher, appraisal, sde, images), portraits);
        LootOverview.LootCorrected += () => _ = _ReloadAfterCorrectionAsync();
        _runChangesSubscription = runChanges?.Subscribe(_OnRunsChangedAsync);
    }

    public ActivitySection Activity { get; } = new() { Title = "ACTIVITY", IsExpanded = true };
    public ActivitySection Rewards { get; } = new() { Title = "REWARDS", IsExpanded = true };
    public ActivitySection Enemies { get; } = new() { Title = "ENEMIES", IsExpanded = true };
    public ActivitySection Fleet { get; } = new() { Title = "FLEET", IsExpanded = true };
    public ActivitySection Bounty { get; } = new() { Title = "BOUNTY", IsExpanded = true };
    public ActivitySection Loot { get; } = new() { Title = "LOOT", IsExpanded = true };
    public ActivitySection Escalation { get; } = new() { Title = "ESCALATION", IsExpanded = true };

    public ObservableCollection<ActivityRewardRowViewModel> RewardRows { get; } = [];
    public ObservableCollection<ActivityEnemyRowViewModel> EnemyRows { get; } = [];
    public ObservableCollection<ActivityEnemyCharacterRowViewModel> EnemyCharacterRows { get; } = [];
    public ObservableCollection<ActivityRunRowViewModel> RunRows { get; } = [];
    public ObservableCollection<ActivityBountyRowViewModel> BountyRows { get; } = [];

    /// <summary>The LOOT section, grouped by character and correctable (ET-215) — the same component the run window
    /// shows, so a saved activity and a running one read the same.</summary>
    public ActivityLootViewModel LootOverview { get; }

    [ObservableProperty] private string _siteText = string.Empty;
    [ObservableProperty] private string _kindText = string.Empty;
    [ObservableProperty] private MaterialIconKind _typeIcon;
    [ObservableProperty] private string _durationText = string.Empty;
    [ObservableProperty] private string _startText = string.Empty;
    [ObservableProperty] private string _endText = string.Empty;

    /// <summary>Everything this activity earned, as prominent as <see cref="DurationText"/> (ET-210 review finding,
    /// 2026-09-09: the total was there, split over three sections, and never once shown as one figure). What it
    /// adds: <see cref="ActivityDetailDto.BountyIsk"/> (actual gamelog payouts), <see cref="ActivityDetailDto.LootIskNet"/>
    /// when there is a priced figure to add, and every ISK-denominated reward form (<c>Isk</c>, <c>BonusIsk</c>,
    /// <c>FixedPayout</c>, <c>Escrow</c>) — deliberately NOT <c>RunParameterKey.Bounty</c>, a mission's own stated
    /// reward line, so a mission that also logged gamelog kills never counts the same ISK twice under two names.
    /// LP and Evermarks have no ISK rate to convert against and are left out, the same rule the REWARDS section
    /// already applies to them.</summary>
    [ObservableProperty] private bool _hasTotalIsk;
    [ObservableProperty] private string _totalIskText = string.Empty;

    /// <summary>Whether the duration above it was measured or typed. The corrected moments overwrite the start and
    /// stop, so the figure itself can no longer say which of the two it is (ET-98).</summary>
    [ObservableProperty] private string _timeSourceText = string.Empty;

    /// <summary>Why there is nothing on screen, when there is nothing — a failed lookup is a state, not silence.</summary>
    [ObservableProperty] private string? _statusMessage;

    [ObservableProperty] private bool _isAgentShown;
    [ObservableProperty] private string _agentText = string.Empty;
    [ObservableProperty] private string _missionLevelText = string.Empty;
    [ObservableProperty] private string _locationText = string.Empty;
    [ObservableProperty] private string _signatureText = string.Empty;
    [ObservableProperty] private bool _isSignatureShown;
    [ObservableProperty] private string _fitText = string.Empty;
    [ObservableProperty] private string? _objectivesText;
    [ObservableProperty] private bool _hasObjectives;

    [ObservableProperty] private bool _isRewardsShown;
    [ObservableProperty] private bool _isBountyShown;
    [ObservableProperty] private bool _isLootShown;
    [ObservableProperty] private bool _isEscalationShown;

    /// <summary>The sections this kind does not have, each with the reason. The distinguishing line of the screen:
    /// an absent section that says nothing is indistinguishable from an empty one.</summary>
    [ObservableProperty] private string? _absentSectionsText;

    [ObservableProperty] private string? _rewardsEmptyText;
    [ObservableProperty] private string? _enemiesEmptyText;

    /// <summary>Whether any character logged a counted sighting at all — the same "no figure for nobody" rule
    /// <see cref="HasBountyFigures"/> follows, so the per-character breakdown and its total do not show a false
    /// zero (ET-210 review finding, 2026-09-09, round 4).</summary>
    [ObservableProperty] private bool _hasEnemyFigures;
    [ObservableProperty] private string _enemyTotalCountText = string.Empty;

    [ObservableProperty] private string? _bountyEmptyText;
    [ObservableProperty] private string? _lootEmptyText;
    [ObservableProperty] private string? _escalationEmptyText;

    [ObservableProperty] private bool _hasBountyFigures;
    [ObservableProperty] private string _bountyText = string.Empty;

    /// <summary>A run of this activity had its loot corrected after it was published (ET-215): the server still
    /// shows the older figures. Said on the screen for as long as it is true — never left for the pilot to find out
    /// from someone reading the server — and never fixed by pushing anything on the pilot's behalf.</summary>
    [ObservableProperty] private bool _isPublishedCopyBehind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepublishText))]
    [NotifyCanExecuteChangedFor(nameof(RepublishCommand))]
    private bool _isRepublishing;

    public string PublishedCopyText =>
        "Changed since it was published. The server still has the old figures until you publish it again.";

    public bool CanRepublish => _republish is not null;

    public string RepublishText => IsRepublishing ? "PUBLISHING…" : "PUBLISH AGAIN";

    /// <summary>Understated on purpose (ET-214, Jithran's own review of the first round): the weight of the decision
    /// sits in the confirmation, not in how loud the control reading it is. True whenever at least one of this
    /// activity's runs is this machine's own — a fully foreign activity (every run read-only, ET-215) has nothing
    /// here to delete.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private bool _canDelete;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteText))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private bool _isDeleting;

    public string DeleteText => IsDeleting ? "Deleting…" : "Delete this activity";

    /// <summary>Set once the delete leaves nothing behind at all — the whole group was this machine's own, or it was
    /// a lone run. A mixed group with a fleetmate's run still in it never reaches this: <see cref="DeleteAsync"/>
    /// re-reads the activity afterwards and, finding it still there, reloads in place instead (ET-214).</summary>
    [ObservableProperty] private bool _isDeleted;

    public string DeletedMessage => "This activity has been deleted.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UndoDeleteText))]
    private bool _isUndoingDelete;

    public string UndoDeleteText => IsUndoingDelete ? "Restoring…" : "Undo";

    [ObservableProperty] private string _participantCountText = string.Empty;

    /// <summary>Null once every run in this activity carries a recorded <see cref="ActivityRunDetailDto.CharacterNameSnapshot"/>
    /// (ET-212 AC-2) — an activity saved entirely after that column existed needs no caveat, because the names on
    /// screen are then read from storage rather than reconstructed from whoever happens to still be logged in.</summary>
    [ObservableProperty] private string? _fleetBasisText;

    [ObservableProperty] private string? _escalationText;
    [ObservableProperty] private string? _escalationObservedText;
    [ObservableProperty] private string? _escalationSystemText;
    [ObservableProperty] private string? _escalationExpiresAtText;
    [ObservableProperty] private string? _escalationJumpsText;
    [ObservableProperty] private string? _escalationJumpsEmptyText;

    public void RefreshModule() => _ = LoadAsync();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Result<ActivityDetailDto> detail =
            await _dispatcher.Query(new GetActivityDetailQuery(_activitySummaryId), cancellationToken);
        if (!detail.IsSuccess || detail.Value is null)
        {
            StatusMessage = detail.Messages.Count > 0 ? detail.Messages[0].Text : "The activity could not be read.";
            return;
        }

        StatusMessage = null;
        _lastDetail = detail.Value;
        (string? escalationJumpsText, string? escalationJumpsEmptyText) =
            await _EscalationJumpsAsync(detail.Value, cancellationToken);
        _Apply(detail.Value, escalationJumpsText, escalationJumpsEmptyText);
        await _ApplyLootBlocksAsync(detail.Value, cancellationToken);
    }

    /// <summary>
    /// A correction in the LOOT section landed and the summary behind this screen is already rebuilt (the command
    /// does that, once, before it returns). The block that was corrected re-read itself; what is left is everything
    /// the summary feeds — TOTAL ISK, the section's own header, and whether the published copy is now behind.
    /// Nothing else is read again: the other blocks, and the escalation's ESI route, did not change.
    /// </summary>
    private async Task _ReloadAfterCorrectionAsync()
    {
        Result<ActivityDetailDto> detail = await _dispatcher.Query(new GetActivityDetailQuery(_activitySummaryId));
        if (!detail.IsSuccess || detail.Value is null)
        {
            StatusMessage = detail.Messages.Count > 0 ? detail.Messages[0].Text : "The activity could not be read.";
            return;
        }

        _lastDetail = detail.Value;
        _ApplyHeader(detail.Value);
        _ApplyLoot(detail.Value);
        _ApplyTotalIsk(detail.Value);
    }

    /// <summary>
    /// A run changed somewhere else — a sync, a second window, the runs screen (ET-222). Until this, an open detail
    /// screen only ever moved for what it did itself. Read again only when the change reached this activity, and never
    /// on top of this screen's own delete, undo or publish: each of those reads the activity again once it lands, and
    /// a second read racing it could land last with what was there before.
    ///
    /// The sections are updated in place — the same objects, open or folded as the reader left them — and a loot
    /// block with a correction open waits for it (<see cref="RunLootViewModel.LoadWhenIdleAsync"/>). An activity that
    /// is gone shows ET-214's deleted state, Undo included; one that was restored follows its runs back.
    /// </summary>
    private async Task _OnRunsChangedAsync(RunChangeBatch changed)
    {
        if (IsDeleting || IsUndoingDelete || IsRepublishing || _lastDetail is not { } shown
            || !changed.Concerns(shown.Runs.Select(run => run.RunId), shown.GroupCode))
            return;

        Result<ActivityDetailDto> detail = await _dispatcher.Query(new GetActivityDetailQuery(_activitySummaryId));
        if (detail.Messages.Any(message => message.Code == MessageCodes.NotFound))
        {
            if (await _FindAgainAsync(shown.GroupCode, _LoneRunIdOf(shown)) is not { } foundId)
            {
                _ShowDeleted(shown);
                return;
            }

            _activitySummaryId = foundId;
            detail = await _dispatcher.Query(new GetActivityDetailQuery(_activitySummaryId));
        }

        if (!detail.IsSuccess || detail.Value is not { } current)
        {
            StatusMessage = detail.Messages.Count > 0 ? detail.Messages[0].Text : "The activity could not be read.";
            return;
        }

        StatusMessage = null;
        IsDeleted = false;
        _lastDetail = current;
        // The jump count is a live ESI read (ET-127); only a changed destination is worth asking it again for.
        (string? escalationJumpsText, string? escalationJumpsEmptyText) =
            _EscalationDestinationOf(current) == _EscalationDestinationOf(shown)
                ? (EscalationJumpsText, EscalationJumpsEmptyText)
                : await _EscalationJumpsAsync(current, CancellationToken.None);
        _Apply(current, escalationJumpsText, escalationJumpsEmptyText);
        await _ApplyLootBlocksAsync(current, CancellationToken.None);
    }

    /// <summary>The deleted state for a delete made somewhere else, set up exactly as <see cref="DeleteAsync"/> sets
    /// it up for its own, so Undo here restores what went.</summary>
    private void _ShowDeleted(ActivityDetailDto shown)
    {
        if (IsDeleted)
            return;

        _deletedGroupCode = shown.GroupCode;
        _deletedRunId = _LoneRunIdOf(shown);
        IsDeleted = true;
        CanDelete = false;
    }

    /// <summary>The activity these runs make up now, by the key its summary is built on — the group code, or the run
    /// itself when it has none. An activity deleted whole lost its summary row, and one restored before ET-222 made
    /// that id stable came back under a new one.</summary>
    private async Task<Guid?> _FindAgainAsync(string? groupCode, Guid? loneRunId)
    {
        Result<IReadOnlyList<ActivityOverviewRowDto>> overview = await _dispatcher.Query(new GetActivityOverviewQuery());
        if (overview is not { IsSuccess: true, Value: { } rows })
            return null;

        ActivityOverviewRowDto? found = groupCode is not null
            ? rows.FirstOrDefault(row => row.GroupCode == groupCode)
            : rows.FirstOrDefault(row => row.RunId == loneRunId);
        return found?.ActivitySummaryId;
    }

    private static Guid? _LoneRunIdOf(ActivityDetailDto detail) =>
        detail.GroupCode is null ? detail.Runs.FirstOrDefault()?.RunId : null;

    private static string? _EscalationDestinationOf(ActivityDetailDto detail) =>
        detail.Parameters.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.EscalationSolarSystemId)
            ?.TypedValue;

    [RelayCommand(CanExecute = nameof(CanStartRepublish))]
    private async Task RepublishAsync()
    {
        if (_republish is null)
            return;

        IsRepublishing = true;
        try
        {
            await _republish();
            await _ReloadAfterCorrectionAsync();
        }
        finally
        {
            IsRepublishing = false;
        }
    }

    private bool CanStartRepublish() => !IsRepublishing;

    /// <summary>
    /// Delete the whole activity (ET-214): every one of this machine's own runs in the group, or the lone run when
    /// it was never grouped. A fleetmate's run pulled from a server (ET-215) is read-only on this very screen for
    /// the same reason it is never touched here — a local soft delete flips <c>SyncState</c> to <c>Pending</c>, and
    /// a run under someone else's character id sitting Pending would be this machine offering to push a change to a
    /// run it does not own. <see cref="DeleteRunsInGroupCommand.OnlyRunIds"/> exists for exactly this: it restricts
    /// the bulk delete to this machine's own runs, leaving a fleetmate's row in the group untouched.
    ///
    /// What happens next depends on what is left. Nothing at all — the summary the delete's own rebuild just
    /// produced is gone, and <see cref="IsDeleted"/> says so. Still someone else's run there — the same summary id
    /// survives a rebuild (ET-215), so re-reading it finds the smaller activity and the screen reloads in place,
    /// <see cref="CanDelete"/> now false because nothing local is left to remove.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartDelete))]
    private async Task DeleteAsync()
    {
        if (_dialogs is null || _lastDetail is not { } detail)
            return;

        List<Guid> ownRunIds = _OwnRunIds(detail);
        if (ownRunIds.Count == 0)
            return;

        if (!await _dialogs.ConfirmAsync("Delete this activity?", _WhatDeletingRemoves(detail, ownRunIds)))
            return;

        IsDeleting = true;
        try
        {
            DateTime deletedAtUtc = DateTime.UtcNow;
            Result outcome = detail.GroupCode is { } groupCode
                ? await _dispatcher.Send(new DeleteRunsInGroupCommand(groupCode, deletedAtUtc, ownRunIds))
                : await _dispatcher.Send(new DeleteRunCommand(ownRunIds[0], deletedAtUtc));
            if (!outcome.IsSuccess)
            {
                StatusMessage = outcome.Messages.Count > 0 ? outcome.Messages[0].Text : "The activity could not be deleted.";
                return;
            }

            _deletedGroupCode = detail.GroupCode;
            _deletedRunId = detail.GroupCode is null ? ownRunIds[0] : null;

            Result<ActivityDetailDto> stillThere = await _dispatcher.Query(new GetActivityDetailQuery(_activitySummaryId));
            if (stillThere is { IsSuccess: true, Value: { } remaining })
            {
                _lastDetail = remaining;
                (string? escalationJumpsText, string? escalationJumpsEmptyText) =
                    await _EscalationJumpsAsync(remaining, CancellationToken.None);
                _Apply(remaining, escalationJumpsText, escalationJumpsEmptyText);
                await _ApplyLootBlocksAsync(remaining, CancellationToken.None);
            }
            else
            {
                IsDeleted = true;
                CanDelete = false;
            }
        }
        finally
        {
            IsDeleting = false;
        }
    }

    private bool CanStartDelete() => CanDelete && !IsDeleting;

    /// <summary>Puts a deleted activity straight back — the soft delete's whole point (ET-214). Same either/or as
    /// the delete itself: a group code restores every run deleted with it, a lone run restores by its own id.</summary>
    [RelayCommand]
    private async Task UndoDeleteAsync()
    {
        if (!IsDeleted)
            return;

        IsUndoingDelete = true;
        try
        {
            Result outcome = _deletedGroupCode is { } groupCode
                ? await _dispatcher.Send(new RestoreRunsInGroupCommand(groupCode))
                : await _dispatcher.Send(new RestoreRunCommand(_deletedRunId!.Value));
            if (!outcome.IsSuccess)
            {
                StatusMessage = outcome.Messages.Count > 0 ? outcome.Messages[0].Text : "The activity could not be restored.";
                return;
            }

            // The delete's own rebuild removed the ActivitySummary row outright. Since ET-222 the restore builds it
            // back under the same id, but an activity whose summary predates that comes back under a new one — find
            // it by its runs before asking for it by an id that may no longer exist.
            if (await _FindAgainAsync(_deletedGroupCode, _deletedRunId) is { } restoredId)
                _activitySummaryId = restoredId;

            IsDeleted = false;
            await LoadAsync();
        }
        finally
        {
            IsUndoingDelete = false;
        }
    }

    private List<Guid> _OwnRunIds(ActivityDetailDto detail) =>
        [.. detail.Runs.Where(run => _ownCharacterIds is null || _ownCharacterIds.Contains(run.CharacterId))
            .Select(run => run.RunId)];

    /// <summary>Named the way the screen already reads it: which site, how many of your own runs, how much ISK —
    /// exactly what ET-214 asks the confirmation to say, built entirely from what is already loaded rather than a
    /// second read. Names a fleetmate's run that stays behind rather than quietly saying nothing about it, and says
    /// what a delete does to a copy already on, or queued for, a server.</summary>
    private string _WhatDeletingRemoves(ActivityDetailDto detail, IReadOnlyCollection<Guid> ownRunIds)
    {
        int foreignCount = detail.Runs.Count - ownRunIds.Count;
        string participants = ownRunIds.Count == 1 ? "1 of your own runs" : $"{ownRunIds.Count} of your own runs";
        string reward = HasTotalIsk ? TotalIskText : "no ISK recorded";
        string foreign = foreignCount switch
        {
            0 => string.Empty,
            1 => " One run from another pilot stays, since it is not yours to remove.",
            _ => $" {foreignCount} runs from other pilots stay, since they are not yours to remove."
        };
        string server = detail.Runs.Any(run => run.SyncState is RunSyncState.Synced or RunSyncState.Outdated)
            ? " It already reached a coupled server — this only removes your own local copy, never the server's."
            : detail.Runs.Any(run => run.SyncState is RunSyncState.Pending)
                ? " It is still queued for a server and has not arrived yet — deleting it here cancels that push."
                : string.Empty;
        return $"{SiteText} goes, with {participants} and {reward}.{foreign}{server}";
    }

    private void _Apply(ActivityDetailDto detail, string? escalationJumpsText, string? escalationJumpsEmptyText)
    {
        _ApplyHeader(detail);
        _ApplyActivity(detail);
        _ApplyRewards(detail);
        _ApplyEnemies(detail);
        _ApplyFleet(detail);
        _ApplyBounty(detail);
        _ApplyLoot(detail);
        _ApplyEscalation(detail, escalationJumpsText, escalationJumpsEmptyText);
        _ApplySectionsPerKind(detail);
        // After Bounty, Loot and Rewards: the total is built from what each of them just settled.
        _ApplyTotalIsk(detail);
        CanDelete = _OwnRunIds(detail).Count > 0;
    }

    private void _ApplyHeader(ActivityDetailDto detail)
    {
        SiteText = detail.SiteName ?? "site not recorded";
        // Same catalogue the run window and the runs overview read (ET-226) — a mission never reads "not known
        // yet" here, and a site with no recorded scanner group reads "Site", never "Combat Site".
        RunTypeDefinition type = RunTypeCatalogue.For(RunTypeResolver.Resolve(detail.ActivityKind, detail.SignatureGroupSnapshot));
        KindText = type.Name;
        TypeIcon = type.Icon;
        DurationText = TimeSpan.FromSeconds(detail.DurationSeconds).ToString(@"hh\:mm\:ss");
        StartText = detail.StartedAtUtc.ToLocalTime().ToString("HH:mm:ss");
        EndText = detail.StoppedAtUtc is { } stoppedAtUtc
            ? stoppedAtUtc.ToLocalTime().ToString("HH:mm:ss")
            : "still open";
        TimeSourceText = detail.Runs.Any(run => run.TimesCorrectedAtUtc is not null)
            ? "times corrected by hand"
            : "measured";
    }

    private void _ApplyActivity(ActivityDetailDto detail)
    {
        ActivityRunDetailDto? source = detail.Runs.FirstOrDefault();
        // Only a mission carries an agent, so the row is not there for anything else — a line that could only ever
        // read "not applicable" teaches the reader about our catalogue, not about their run.
        IsAgentShown = detail.ActivityKind == ActivityKind.Mission;
        ActivityRunDetailDto? withAgent = detail.Runs.FirstOrDefault(run => run.AgentId is not null);
        AgentText = withAgent?.AgentId is { } agentId ? $"agent {agentId}" : "not recorded";
        MissionLevelText = withAgent?.MissionLevel is { } level ? $"Level {level}" : "not recorded";
        LocationText = _LocationText(detail.SolarSystemId);
        SignatureText = source?.Signature ?? string.Empty;
        IsSignatureShown = !string.IsNullOrWhiteSpace(source?.Signature);
        FitText = source?.FitNameSnapshot ?? "not recognised";

        // Mission counters, and only those: the reward forms belong under REWARDS, the escalation under its own
        // section. They stay empty until somebody types them — nothing plausible is filled in for them.
        string[] objectives = [.. detail.Parameters
            .Where(parameter => parameter.ParameterKey is RunParameterKey.Smugglers or RunParameterKey.Civilians)
            .Select(parameter => $"{parameter.ParameterKey.ToString().ToLowerInvariant()} {parameter.TypedValue}")];
        HasObjectives = objectives.Length > 0;
        ObjectivesText = HasObjectives ? string.Join(" · ", objectives) : null;

        Activity.HeaderSummary = detail.ActivityKind == ActivityKind.Mission
            ? $"{AgentText} · {MissionLevelText}"
            : $"{KindText} · {LocationText}";
    }

    /// <summary>The system a run was on, named through the local SDE (ET-213) — never ESI, and never the bare id
    /// the store carries (a regression from ET-210/ET-130: <c>Run.SolarSystemId</c> only started being recorded for
    /// a site run then, and this screen never learned to turn it into a name). The same reading the run window
    /// already gives live, <c>ActivityWindowViewModel.LocationText</c>: a plain name, no security status, because
    /// the window itself does not show one either. A stored id the SDE does not carry — an older build, a boundary
    /// case — falls back to the id itself rather than a blank line or an error: still a readable place, just not a
    /// name anyone typed.</summary>
    private string _LocationText(int? solarSystemId) =>
        solarSystemId is not { } id
            ? "not recorded"
            : _sde?.GetSolarSystem(id)?.Name ?? $"system {id}";

    private void _ApplyRewards(ActivityDetailDto detail)
    {
        RewardRows.Clear();
        // Everything that is not claimed by another section, rather than a list of the keys known when this was
        // written: RunParameterKey only ever grows, and a key nobody special-cased must show up rather than vanish.
        foreach (RunParameterDto parameter in detail.Parameters.Where(_IsRewardParameter))
            RewardRows.Add(new ActivityRewardRowViewModel(parameter));

        RewardsEmptyText = RewardRows.Count > 0 ? null : "No reward was recorded for this activity.";
        Rewards.HeaderSummary = RewardRows.Count > 0
            ? string.Join(" · ", RewardRows.Select(row => $"{row.ValueText} {row.Label}"))
            : "nothing recorded";
    }

    private void _ApplyEnemies(ActivityDetailDto detail)
    {
        EnemyRows.Clear();
        foreach (RunEnemyObservationDto observation in detail.EnemyObservations)
            EnemyRows.Add(new ActivityEnemyRowViewModel(observation));

        // Not "no combat was measured": SaveRunCommandHandler stores only the rows that carry a count, and the count
        // is typed by hand (ET-106). An empty list therefore means nobody counted, and says nothing at all about
        // whether there was a fight — which the BOUNTY figure three lines down often disproves outright.
        EnemiesEmptyText = EnemyRows.Count > 0
            ? null
            : "Enemies are saved only once you count them, and none were counted here. Whether there was combat is "
              + "not recorded either way.";
        Enemies.HeaderSummary = EnemyRows.Count > 0
            ? $"{detail.EnemyObservations.Sum(observation => observation.Count)} counted · " +
              $"{detail.EnemyObservations.Select(observation => observation.EnemyTypeId).Distinct().Count()} types"
            : "none counted";

        // One row per character, summed across every type they logged — the same breakdown BOUNTY already gives
        // (ET-210 review finding, 2026-09-09, round 4: Jithran chose per-character tracking with a group total,
        // not one shared tally). Largest contribution first, same ordering rule as the bounty breakdown.
        EnemyCharacterRows.Clear();
        Dictionary<Guid, long> characterByRun = detail.Runs.ToDictionary(run => run.RunId, run => run.CharacterId);
        foreach (IGrouping<long, RunEnemyObservationDto> group in detail.EnemyObservations
                     .Where(observation => characterByRun.ContainsKey(observation.RunId))
                     .GroupBy(observation => characterByRun[observation.RunId])
                     .OrderByDescending(group => group.Sum(observation => observation.Count)))
            EnemyCharacterRows.Add(new ActivityEnemyCharacterRowViewModel(
                group.Key, group.Sum(observation => observation.Count), id => _ResolveName(detail, id)));

        HasEnemyFigures = EnemyCharacterRows.Count > 0;
        int total = detail.EnemyObservations.Sum(observation => observation.Count);
        EnemyTotalCountText = total == 1 ? "1 enemy" : $"{total} enemies";
    }

    private void _ApplyFleet(ActivityDetailDto detail)
    {
        RunRows.Clear();
        foreach (ActivityRunDetailDto run in detail.Runs)
            RunRows.Add(new ActivityRunRowViewModel(run, _nameOf));

        ParticipantCountText = $"{detail.ParticipantCount} participants";
        // Only once every run here is missing its own recorded name (ET-212) does the caveat still apply — an
        // activity saved entirely after CharacterNameSnapshot existed has nothing left to explain away. A mixed
        // activity (an old run beside a new one, or one synced from a fleetmate's older client) still gets the
        // caveat: some of the names on screen below are still a live lookup or a bare id, not a stored fact.
        FleetBasisText = detail.Runs.Count > 0 && detail.Runs.All(run => !string.IsNullOrEmpty(run.CharacterNameSnapshot))
            ? null
            : "Participant names are not recorded yet, so these are the runs behind this activity by " +
              "character id. The count above is real: it comes from the activity's own distinct characters.";
        Fleet.HeaderSummary = $"{ParticipantCountText} · {detail.PayoutEligibleCount} sharing";
    }

    private void _ApplyBounty(ActivityDetailDto detail)
    {
        // Not "0 ISK": BountyIsk is zero both when nothing was shot and when nothing was measured, and only the
        // absence of bounty rows tells those apart.
        HasBountyFigures = detail.BountyEntries.Count > 0;
        BountyText = IskFormat.Whole(detail.BountyIsk);
        BountyEmptyText = HasBountyFigures
            ? null
            : "No bounty line came past in the game log for this activity.";
        Bounty.HeaderSummary = HasBountyFigures
            ? $"{BountyText} · {detail.BountyEntries.Count} payouts"
            : "nothing measured";

        // One row per character, the same breakdown the FLEET section already gives for participation (ET-210
        // review finding, 2026-09-09) — largest share first, so the reader sees who brought in the most without
        // having to scan every row.
        BountyRows.Clear();
        Dictionary<Guid, long> characterByRun = detail.Runs.ToDictionary(run => run.RunId, run => run.CharacterId);
        foreach (IGrouping<long, RunBountyEntryDto> group in detail.BountyEntries
                     .Where(entry => characterByRun.ContainsKey(entry.RunId))
                     .GroupBy(entry => characterByRun[entry.RunId])
                     .OrderByDescending(group => group.Sum(entry => entry.Isk)))
            BountyRows.Add(new ActivityBountyRowViewModel(group.Key, group.Sum(entry => entry.Isk),
                id => _ResolveName(detail, id)));
    }

    /// <summary>One character's name for this activity (ET-212): whichever of their own runs recorded one at start
    /// time wins, since every run of the same character in one activity carries the same pilot. Falls back to the
    /// live roster lookup the constructor was handed, then to the bare id — the exact chain this screen always used
    /// before a name could be stored at all, so an activity saved before this column existed reads unchanged.</summary>
    private string _ResolveName(ActivityDetailDto detail, long characterId) =>
        detail.Runs.Where(run => run.CharacterId == characterId)
            .Select(run => run.CharacterNameSnapshot)
            .FirstOrDefault(name => !string.IsNullOrEmpty(name))
        ?? _nameOf?.Invoke(characterId)
        ?? $"character {characterId}";

    private void _ApplyTotalIsk(ActivityDetailDto detail)
    {
        decimal rewardIsk = detail.Parameters
            .Where(parameter => parameter.ParameterKey is RunParameterKey.Isk or RunParameterKey.BonusIsk
                or RunParameterKey.FixedPayout or RunParameterKey.Escrow)
            .Sum(parameter => parameter.Amount.GetValueOrDefault());
        decimal total = detail.BountyIsk + detail.LootIskNet.GetValueOrDefault() + rewardIsk;

        // Never a zero for a figure nobody offered: an activity with no bounty, no priced loot and no ISK-form
        // reward has nothing to show here, same rule every other figure on this screen follows.
        HasTotalIsk = detail.BountyIsk > 0 || detail.LootIskNet is not null || rewardIsk > 0;
        TotalIskText = IskFormat.Whole(total);
    }

    /// <summary>What the summary says about the loot, and whether the server's copy is still the same. The figures in
    /// the header are the summary's own — the same numbers the runs overview and TOTAL ISK are made of.</summary>
    private void _ApplyLoot(ActivityDetailDto detail)
    {
        RunLootCaptureDto[] captures = [.. detail.Runs.SelectMany(run => run.LootCaptures)];
        LootEmptyText = captures.Length > 0
            ? null
            : "No loot capture was recorded for this activity — nothing was copied, so there is nothing to value.";
        Loot.HeaderSummary = captures.Length > 0
            ? $"{_IskOrNoPrice(detail.LootIskNet)} · {captures.Length} captures · {captures.Count(capture => capture.IsExcluded)} excluded"
            : "nothing captured";
        IsPublishedCopyBehind = detail.Runs.Any(run => run.SyncState is RunSyncState.Outdated);
    }

    /// <summary>
    /// One block per run, each showing that run's own captures (ET-215) — the grouping ET-211 made true, since a
    /// capture now lands on the run of the character who copied it. A run from before that carries the whole
    /// group's loot and the others carry none, and that is exactly how it is shown: no share is worked out after
    /// the fact that was never recorded. Largest first on the way in, like BOUNTY and ENEMIES; a later re-read keeps
    /// the order, so a correction never moves the block the pilot is working in.
    /// </summary>
    private async Task _ApplyLootBlocksAsync(ActivityDetailDto detail, CancellationToken cancellationToken)
    {
        bool isFirstRead = LootOverview.Characters.Count == 0;
        foreach (ActivityRunDetailDto run in detail.Runs)
        {
            ActivityLootCharacterViewModel block = LootOverview.Show(run.RunId, run.CharacterId,
                _ResolveName(detail, run.CharacterId));
            block.Loot.IsLocked = true;
            block.Loot.IsReadOnly = _ownCharacterIds is { } own && !own.Contains(run.CharacterId);
            await block.Loot.LoadWhenIdleAsync(run.LootCaptures, cancellationToken);
        }

        LootOverview.Keep([.. detail.Runs.Select(run => run.RunId)]);
        if (isFirstRead)
            LootOverview.OrderByValue();
    }

    private void _ApplyEscalation(ActivityDetailDto detail, string? escalationJumpsText, string? escalationJumpsEmptyText)
    {
        RunParameterDto? escalation = detail.Parameters
            .FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.Escalation);
        EscalationText = escalation?.TypedValue;
        EscalationObservedText = escalation is null
            ? null
            : $"read from the Agency at {escalation.ObservedAtUtc.ToLocalTime():HH:mm} on " +
              $"{escalation.ObservedAtUtc.ToLocalTime():d MMM}";
        EscalationEmptyText = escalation is null ? "No escalation has been registered for this activity." : null;
        Escalation.HeaderSummary = escalation?.TypedValue ?? "none registered";

        EscalationSystemText = detail.Parameters
            .FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.EscalationSystem)?.TypedValue;
        EscalationExpiresAtText = detail.Parameters
                .FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.EscalationExpiresAtUtc)?.TypedValue
            is { } expiresAt && DateTime.TryParse(expiresAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime expiresAtUtc)
            ? $"expires {expiresAtUtc.ToLocalTime():HH:mm} on {expiresAtUtc.ToLocalTime():d MMM}"
            : null;
        EscalationJumpsText = escalationJumpsText;
        EscalationJumpsEmptyText = escalationJumpsEmptyText;
    }

    /// <summary>
    /// The jump count to the escalation's destination, read fresh at display time and never stored (ET-127) — an
    /// escalation typed offline stays fully usable; only this one line needs ESI, through the existing
    /// <see cref="IEsiClient"/> and its own disk-level cache. <see cref="EscalationJumpsEmptyText"/> carries why
    /// whenever the count itself is null, the same "empty is a state, not silence" rule as
    /// <see cref="EscalationEmptyText"/> and every other <c>*EmptyText</c> on this screen — a hidden JUMPS row would
    /// read as "this escalation has no destination", which is a different fact from "the count could not be read".
    /// The pair returns (null, null) only when there is no destination to count a distance to at all.
    ///
    /// Origin is the character's current location rather than the run's own system: ET-124 never established which
    /// of the two the Agency counts from, and the ticket's own escape hatch for that unknown is to read from
    /// wherever the pilot actually is right now and label the line accordingly (AC-3).
    /// </summary>
    private async Task<(string? Text, string? EmptyText)> _EscalationJumpsAsync(
        ActivityDetailDto detail, CancellationToken cancellationToken)
    {
        if (detail.Parameters.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.EscalationSolarSystemId)
                ?.TypedValue is not { } typed
            || !int.TryParse(typed, CultureInfo.InvariantCulture, out int destinationSystemId))
            return (null, null);

        if (_esi is null || _locations is null)
            return (null, "Jump count not available: no ESI connection.");

        if (detail.Runs.FirstOrDefault()?.CharacterId is not { } characterId)
            return (null, "Jump count not available: no character recorded on this run.");

        var location = await _locations.GetLocationAsync(checked((int)characterId), cancellationToken);
        if (location is not { IsSuccess: true, Value: { } here })
            return (null, "Jump count not available: the pilot's current location could not be read.");

        var route = await _esi.GetAsync<int[]>($"/route/{here.SolarSystemId}/{destinationSystemId}/",
            cancellationToken: cancellationToken, expectedNotFound: true);
        return route is { IsSuccess: true, Value.Length: > 0 }
            ? ($"{route.Value.Length - 1} jumps from here", null)
            : (null, "Jump count not available: no stargate route to this system.");
    }

    /// <summary>
    /// Which sections this kind has, and one line for the ones it does not. ACTIVITY, ENEMIES and FLEET are on every
    /// kind — every run has an identity, a fight that never happened is still a measurement, and every activity has
    /// a crew even when that crew is one pilot. The other four are a judgment about the kind, overridden whenever
    /// there is data for them, because a table is not a reason to hide a stored row.
    /// </summary>
    private void _ApplySectionsPerKind(ActivityDetailDto detail)
    {
        ActivityKind kind = detail.ActivityKind;
        IsRewardsShown = kind == ActivityKind.Mission || RewardRows.Count > 0;
        IsBountyShown = kind is ActivityKind.Abyssal or ActivityKind.Site || HasBountyFigures;
        IsLootShown = kind is ActivityKind.Abyssal or ActivityKind.Site || detail.Runs.Any(run => run.LootCaptures.Count > 0);
        IsEscalationShown = kind == ActivityKind.Site || EscalationText is not null;

        string noun = _KindNoun(kind);
        List<string> absent = [];
        if (!IsRewardsShown)
            absent.Add($"no REWARDS — {noun} pays in what it drops, not in a reward agreed beforehand");
        if (!IsBountyShown)
            absent.Add($"no BOUNTY — {noun} has no rats whose bounty lands in your wallet");
        if (!IsLootShown)
            absent.Add($"no LOOT — {noun} leaves no wrecks to empty");
        if (!IsEscalationShown)
            absent.Add($"no ESCALATION — {noun} does not escalate");

        // Named rather than dropped because a section that is simply missing reads exactly like one that is empty,
        // and the two mean opposite things. The line says which sections and why; it does not explain itself to the
        // reader, who came here to see their run and not the reasoning behind the screen.
        AbsentSectionsText = absent.Count == 0
            ? null
            : $"Not shown for this kind of activity: {string.Join("; ", absent)}.";
    }

    public void Dispose() => _runChangesSubscription?.Dispose();

    private static bool _IsRewardParameter(RunParameterDto parameter) =>
        parameter.ParameterKey is not (RunParameterKey.Escalation or RunParameterKey.EscalationDungeonId
            or RunParameterKey.EscalationSystem or RunParameterKey.EscalationSolarSystemId
            or RunParameterKey.EscalationExpiresAtUtc or RunParameterKey.Smugglers or RunParameterKey.Civilians);

    /// <summary>"no price" and not "0 ISK": a figure nobody has must not look like a figure that came out at zero
    /// (ET-65 AC-5).</summary>
    private static string _IskOrNoPrice(decimal? isk) => IskFormat.WholeOrNoPrice(isk);

    // Default arm rather than a throw: ActivityKind is stored by value and only ever grows, and a kind this screen
    // has never heard of should read a little vaguer, not take the window down (AGENTS.md §2).
    private static string _KindNoun(ActivityKind kind) => kind switch
    {
        ActivityKind.Abyssal => "an abyssal pocket",
        ActivityKind.Site => "a site",
        ActivityKind.Mission => "a mission",
        _ => "this kind of activity"
    };
}
