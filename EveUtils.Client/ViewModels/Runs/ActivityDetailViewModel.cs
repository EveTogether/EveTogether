using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Client.Formatting;
using EveUtils.Client.Imaging;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Sde;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// ET-162: one saved activity, fully expanded — the unfolded brother of the activity window, built from the same
/// section framework (ET-236): the activity's type names its sections, and each is a module of its own.
///
/// The screen's own subject is which sections an activity type has, and what a missing one says. Two rules, and
/// they are different on purpose:
/// <list type="bullet">
/// <item>A section the type claims is always drawn. Empty, it carries one line naming what was not measured —
/// never a "0", which reads as a measurement that was taken and came out at nothing.</item>
/// <item>A section the type does not claim is not drawn, and is named in <see cref="AbsentSectionsText"/> with the
/// reason. Dropping it silently would leave the reader unable to tell "there was nothing" from "this type has no
/// such thing".</item>
/// </list>
/// A type that does not claim a section still gets it when there is data for it, so a reward booked against a site
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
    private readonly IReadOnlySet<long>? _ownCharacterIds;
    private readonly Func<Task>? _republish;
    private readonly IDialogService? _dialogs;

    /// <summary>Every detail section there is, in screen order — built once, drawn when the type claims it or the
    /// activity has something for it.</summary>
    private readonly IReadOnlyList<RunDetailSection> _sections;

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
        _ownCharacterIds = ownCharacterIds;
        _republish = republish;
        _dialogs = dialogs;
        var services = new RunDetailSectionServices(dispatcher, appraisal, nameOf, esi, locations, sde, portraits,
            images, ownCharacterIds);
        _sections = [.. RunSectionModules.All
            .Select(module => module.CreateForDetail?.Invoke(services))
            .OfType<RunDetailSection>()];
        foreach (RunDetailSection section in _sections)
            section.ActivityCorrected += () => _ = _ReloadAfterCorrectionAsync();
        _runChangesSubscription = runChanges?.Subscribe(_OnRunsChangedAsync);
    }

    /// <summary>The sections on screen: the ones the activity's type claims, and any other it has data for.</summary>
    public ObservableCollection<RunDetailSection> Sections { get; } = [];

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

    /// <summary>The sections this type does not have, each with the reason. The distinguishing line of the screen:
    /// an absent section that says nothing is indistinguishable from an empty one.</summary>
    [ObservableProperty] private string? _absentSectionsText;

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
        await _ApplyAsync(detail.Value, followUp: false, cancellationToken);
    }

    /// <summary>
    /// A section changed the activity itself — a correction in the LOOT section landed — and the summary behind this
    /// screen is already rebuilt (the command does that, once, before it returns). The block that was corrected
    /// re-read itself; what is left is everything the summary feeds — TOTAL ISK, the sections' own headers, and
    /// whether the published copy is now behind. Nothing else is read again: the other blocks, and the escalation's
    /// ESI route, did not change.
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
        _Apply(detail.Value);
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
        await _ApplyAsync(current, followUp: true, CancellationToken.None);
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
                await _ApplyAsync(remaining, followUp: false, CancellationToken.None);
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

    /// <summary>The whole screen for one read of the activity, then whatever each section reads beyond it.</summary>
    private async Task _ApplyAsync(ActivityDetailDto detail, bool followUp, CancellationToken cancellationToken)
    {
        RunDetailSectionInput input = _Apply(detail);
        foreach (RunDetailSection section in _sections)
            await section.LoadAsync(input, followUp, cancellationToken);
    }

    private RunDetailSectionInput _Apply(ActivityDetailDto detail)
    {
        RunTypeDefinition type = RunTypeCatalogue.For(detail.ActivityKind, detail.SignatureGroupSnapshot);
        var input = new RunDetailSectionInput(detail, type, id => _ResolveName(detail, id));
        _ApplyHeader(detail, type);
        foreach (RunDetailSection section in _sections)
            section.Apply(input);
        _ApplySectionsPerType(type);
        // After the sections: the total is built from the same figures they just settled.
        _ApplyTotalIsk(detail);
        CanDelete = _OwnRunIds(detail).Count > 0;
        return input;
    }

    private void _ApplyHeader(ActivityDetailDto detail, RunTypeDefinition type)
    {
        SiteText = detail.SiteName ?? "site not recorded";
        // Same catalogue the run window and the runs overview read (ET-226) — a mission never reads "not known
        // yet" here, and a site with no recorded scanner group reads "Site", never "Combat Site".
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
        IsPublishedCopyBehind = detail.Runs.Any(run => run.SyncState is RunSyncState.Outdated);
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
        // The general reward sum, less whatever a section says has expired out of it — a mission's own bonus once
        // its time window has passed (ET-237). Never a replacement for the sum: a type with no such rule subtracts
        // nothing.
        decimal rewardIsk = TotalIskCalculator.RewardIsk(
            detail.Parameters.Select(parameter => (parameter.ParameterKey, parameter.Amount)))
            - _sections.Sum(section => section.ExpiredBonusIsk);
        decimal total = detail.BountyIsk + detail.LootIskNet.GetValueOrDefault() + rewardIsk;

        // Never a zero for a figure nobody offered: an activity with no bounty, no priced loot and no ISK-form
        // reward has nothing to show here, same rule every other figure on this screen follows.
        HasTotalIsk = detail.BountyIsk > 0 || detail.LootIskNet is not null || rewardIsk > 0;
        TotalIskText = IskFormat.Whole(total);
    }

    /// <summary>
    /// Which sections this type has, and one line for the ones it does not. A section the type claims is drawn
    /// whatever is in it; any other is drawn when there is data for it — a table is not a reason to hide a stored
    /// row — and otherwise named with its reason.
    /// </summary>
    private void _ApplySectionsPerType(RunTypeDefinition type)
    {
        List<RunDetailSection> shown = [];
        List<string> absent = [];
        foreach (RunDetailSection section in _sections)
        {
            if (type.DetailSections.Contains(section.Id) || section.HasContent)
                shown.Add(section);
            else if (section.AbsentReason(type.Noun) is { } reason)
                absent.Add(reason);
        }

        Sections.ReconcileTo(shown);
        // Named rather than dropped because a section that is simply missing reads exactly like one that is empty,
        // and the two mean opposite things. The line says which sections and why; it does not explain itself to the
        // reader, who came here to see their run and not the reasoning behind the screen.
        AbsentSectionsText = absent.Count == 0
            ? null
            : $"Not shown for this kind of activity: {string.Join("; ", absent)}.";
    }

    public void Dispose() => _runChangesSubscription?.Dispose();
}
