using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
using EveUtils.Client.Formatting;
using EveUtils.Client.ViewModels.Runs.Attendance;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// HOMEFRONT on the detail screen (ET-230): who was in the site at completion as it was decided — N with who decided it
/// and when, the fleet's size at STOP beside it, and the whole flat list, this client's own characters first.
///
/// Presence is shown only as the live fleet view, for a fleet this client is in right now, and said to be exactly that
/// — never a record of who was there at the end (Jithran, 2026-09-11). With no fleet to ask, nothing is shown.
///
/// The one who decided can still change the list after SAVE: the correction lands on this client's own runs, goes to
/// the fleet again as <c>fleet.run-attendance</c>, and marks a published run as behind the server (ET-215) — which for
/// a fleet run publishes itself again (ET-245), so a member offline right now still gets it on their next pull.
/// </summary>
public sealed partial class HomefrontDetailSectionViewModel(RunDetailSectionServices services)
    : RunDetailSection(RunSectionId.Homefront, "HOMEFRONT")
{
    private RunDetailSectionInput? _input;
    private IReadOnlyList<RosterCharacter>? _roster;
    private readonly Dictionary<long, bool> _edits = [];

    public ObservableCollection<AttendanceRowViewModel> Rows { get; } = [];

    [ObservableProperty] private string _inSiteText = string.Empty;

    [ObservableProperty] private string? _decidedByText;

    [ObservableProperty] private string _fleetAtStopText = string.Empty;

    [ObservableProperty] private string? _emptyText;

    [ObservableProperty] private string? _presenceCaption;

    /// <summary>This client decided the list — the fleet's commander, or the pilot over their own run — so it may
    /// correct it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyPropertyChangedFor(nameof(IsEditOffered))]
    private bool _canEdit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditOffered))]
    private bool _isEditing;

    public bool IsEditOffered => CanEdit && !IsEditing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DecreaseNotOnRosterCommand))]
    private int _notOnRosterCount;

    [ObservableProperty] private string? _editError;

    // ── The outcome and the payout (ET-231) ─────────────────────────────────────────────────────────

    /// <summary>"completed", "failed", "unknown", or, for Abyssal Artifact Recovery, "N of 9 waves paid" — read-only
    /// here: the outcome is decided in the run window at STOP, not corrected from this screen.</summary>
    [ObservableProperty] private string _outcomeText = string.Empty;

    /// <summary>"Raid · 5-pilot table, valid from 19 Mar 2026" — which <see cref="HomefrontPayoutTable"/> entry this
    /// activity's own runs were priced against, read straight off the stored column
    /// (<c>Run.HomefrontPayoutTableVersion</c>) rather than recomputed here, so two clients on two app versions
    /// never silently disagree about the same site.</summary>
    [ObservableProperty] private string? _payoutTableVersionText;

    /// <summary>"15,000,000 ISK each" or "N beyond the table" — the fixed payout at N, once the site is decided.</summary>
    [ObservableProperty] private string _payoutSummaryText = string.Empty;

    [RelayCommand]
    private async Task ConfirmAllAsExpectedAsync()
    {
        foreach (AttendanceRowViewModel row in Rows.Where(row => row.CanActOnPayout).ToList())
            if (row.ExpectedPayoutIsk is { } amount)
                await _SetPayoutAsync(row, amount);
    }

    private void _OnConfirmPayout(AttendanceRowViewModel row)
    {
        if (row.ExpectedPayoutIsk is { } amount)
            _ = _SetPayoutAsync(row, amount);
    }

    private void _OnEnterPayout(AttendanceRowViewModel row, decimal amount) => _ = _SetPayoutAsync(row, amount);

    private async Task _SetPayoutAsync(AttendanceRowViewModel row, decimal amount)
    {
        if (_input is not { } input
            || input.Detail.Runs.FirstOrDefault(run => run.CharacterId == row.CharacterId) is not { } ownRun)
            return;

        Result written = await services.Dispatcher.Send(new SetHomefrontPayoutCommand(ownRun.RunId, amount));
        if (written.IsSuccess)
            RaiseActivityCorrected();
    }

    public string EditExplanation =>
        "Changing who was in the site sends the list to every member again; runs already published are marked as " +
        "behind the server until they are published again.";

    public override bool HasContent => _input?.Detail.Attendance is not null;

    public override void Apply(RunDetailSectionInput input)
    {
        _input = input;
        IsEditing = false;
        _edits.Clear();
        _Show();
    }

    public override async Task LoadAsync(RunDetailSectionInput input, bool followUp, CancellationToken cancellationToken)
    {
        // Built for every activity like every detail section; only a homefront's asks the fleet anything.
        if (!input.RunType.PaysPerCharacterInSite || input.Detail.FleetId is not { } fleetId
            || services.Services is not { } provider)
            return;

        _roster = await AttendanceRoster.ReadAsync(provider, fleetId);
        _Show();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Edit()
    {
        _edits.Clear();
        EditError = null;
        IsEditing = true;
        _Show();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        _edits.Clear();
        IsEditing = false;
        _Show();
    }

    [RelayCommand]
    private void IncreaseNotOnRoster() => NotOnRosterCount++;

    [RelayCommand(CanExecute = nameof(CanDecreaseNotOnRoster))]
    private void DecreaseNotOnRoster() => NotOnRosterCount--;

    private bool CanDecreaseNotOnRoster() => NotOnRosterCount > 0;

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (_input is not { } input || !CanEdit)
            return;

        ActivityDetailDto detail = input.Detail;
        RunAttendanceDecision? before = detail.Attendance;
        IReadOnlySet<long> own = services.OwnCharacterIds ?? new HashSet<long>(detail.Runs.Select(run => run.CharacterId));
        int? commander = _CommanderOf(detail.FleetId);
        bool asCommander = before?.Source is AttendanceSource.FleetCommander || commander is not null;
        long setBy = asCommander
            ? commander ?? before?.SetByCharacterId ?? 0
            : before?.SetByCharacterId ?? own.FirstOrDefault(id => detail.Runs.Any(run => run.CharacterId == id));

        // Never stamped at or before the list it corrects: a receiver keeps the newest, and must take this one.
        DateTime nowUtc = DateTime.UtcNow;
        DateTime setAtUtc = RunGroupAttendance.ToWireInstant(
            before is { } previous && previous.SetAtUtc >= nowUtc ? previous.SetAtUtc.AddMilliseconds(1) : nowUtc);
        RunAttendanceDecision decision = new(
            [.. Rows.Select(row => _EntryFor(row, before))], NotOnRosterCount,
            asCommander ? AttendanceSource.FleetCommander : AttendanceSource.Pilot, setBy, setAtUtc);

        Result<int> written = await services.Dispatcher.Send(new SetRunAttendanceCommand(decision, [.. own],
            detail.GroupCode, detail.GroupCode is null ? detail.Runs.FirstOrDefault()?.RunId : null));
        if (!written.IsSuccess)
        {
            EditError = written.Messages.FirstOrDefault()?.Text ?? "The list could not be saved.";
            return;
        }

        if (asCommander && commander is { } fleetCommander && detail.FleetId is { } fleetId && detail.GroupCode is { } groupCode
            && services.Services?.GetService<IEventBus>() is { } eventBus)
            await eventBus.PublishAsync(new FleetRunAttendanceEvent(new RunGroupAttendance(
                    fleetId, groupCode, RunGroupAttendance.ToUnixMs(setAtUtc), decision.Entries, decision.NotOnRosterCount),
                    fleetCommander),
                EventTarget.Remote);

        IsEditing = false;
        _edits.Clear();
        RaiseActivityCorrected();
    }

    // A line left as it was keeps its reason; one changed here was set by hand.
    private static RunAttendanceEntryInput _EntryFor(AttendanceRowViewModel row, RunAttendanceDecision? before) =>
        before?.Entries.FirstOrDefault(entry => entry.CharacterId == row.CharacterId) is { } kept && kept.IsInSite == row.IsInSite
            ? kept
            : new RunAttendanceEntryInput
            {
                CharacterId = row.CharacterId,
                CharacterName = row.Name,
                IsInSite = row.IsInSite,
                IsExternal = row.IsExternal,
                Reason = AttendanceReason.SetByHand
            };

    private void _Show()
    {
        if (_input is not { } input)
            return;

        ActivityDetailDto detail = input.Detail;
        RunAttendanceDecision? decision = detail.Attendance;
        IReadOnlySet<long> own = services.OwnCharacterIds ?? new HashSet<long>(detail.Runs.Select(run => run.CharacterId));
        int? commander = _CommanderOf(detail.FleetId);
        CanEdit = decision is not null
            ? own.Contains(commander ?? decision.SetByCharacterId)
            : detail.Runs.Count > 0 && detail.Runs.All(run => own.Contains(run.CharacterId)) && detail.FleetId is null;

        int? fleetAtStop = detail.Runs.Select(run => run.FleetSizeAtStop).Max();
        FleetAtStopText = fleetAtStop is { } size ? $"{size} characters" : "not recorded";

        if (decision is null && !IsEditing)
        {
            Rows.Clear();
            InSiteText = "not decided";
            DecidedByText = null;
            PresenceCaption = null;
            EmptyText = "Nobody decided who was in the site when this homefront completed.";
            HeaderSummary = "not decided";
            OutcomeText = "not decided";
            PayoutTableVersionText = null;
            PayoutSummaryText = string.Empty;
            return;
        }

        EmptyText = null;
        if (!IsEditing)
            NotOnRosterCount = decision?.NotOnRosterCount ?? 0;

        List<AttendanceRowViewModel> rows = [];
        foreach (RunAttendanceEntryInput entry in _EntriesToShow(detail, decision, own))
        {
            bool isLocal = own.Contains(entry.CharacterId);
            AttendanceRowViewModel row = new(entry.CharacterId, _NameOf(entry, input), isLocal, entry.IsExternal,
                changed => _edits[changed.CharacterId] = changed.IsInSite, _OnConfirmPayout, _OnEnterPayout);
            bool isInSite = _edits.TryGetValue(entry.CharacterId, out bool edited) ? edited : entry.IsInSite;
            row.Show(isInSite, entry.Reason, entry.ReasonAmount, false,
                entry.Reason is AttendanceReason.SetByHand && decision is not null ? _DeciderName(decision, input) : null);
            row.IsEditable = IsEditing;
            row.Presence = services.Services is { } provider && _roster is { } roster && !entry.IsExternal
                ? AttendanceRoster.PresenceOf(provider, entry.CharacterId, isLocal, null,
                    roster.FirstOrDefault(member => member.CharacterId == entry.CharacterId)?.LastSeenAt, DateTimeOffset.UtcNow)
                : null;
            rows.Add(row);
        }

        Rows.Clear();
        foreach (AttendanceRowViewModel row in rows
                     .OrderByDescending(row => row.IsLocal)
                     .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase))
            Rows.Add(row);

        int ticked = rows.Count(row => row.IsInSite);
        int inSite = ticked + NotOnRosterCount;
        InSiteText = $"{inSite} character{(inSite == 1 ? "" : "s")} · {ticked} ticked + {NotOnRosterCount} not on the roster";
        DecidedByText = decision is null
            ? null
            : $"set by {_DeciderName(decision, input)}"
              + (decision.Source is AttendanceSource.FleetCommander ? " (fleet commander)" : string.Empty)
              + $" at {decision.SetAtUtc.ToLocalTime():HH:mm}";
        PresenceCaption = _roster is not null && detail.StoppedAtUtc is { } stoppedAtUtc
            ? $"Online / offline is the live fleet view right now, the same as the fleet shows — not a record of who " +
              $"was there at {stoppedAtUtc.ToLocalTime():HH:mm}."
            : null;
        HeaderSummary = fleetAtStop is { } fleet ? $"{fleet} in fleet · {inSite} in site" : $"{inSite} in site";
        _ShowPayout(input, detail, decision, rows, inSite);
    }

    /// <summary>The fixed payout at N (ET-231), on the section header and on every row — read straight off
    /// <see cref="HomefrontPayoutTable"/>, never recomputed differently on two screens.</summary>
    private void _ShowPayout(RunDetailSectionInput input, ActivityDetailDto detail, RunAttendanceDecision? decision,
        IReadOnlyList<AttendanceRowViewModel> rows, int inSite)
    {
        string? kind = input.RunType.HomefrontKind;
        bool isAar = kind == "Abyssal Artifact Recovery";
        DateTime atUtc = detail.StoppedAtUtc ?? DateTime.UtcNow;
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? table =
            isAar ? null : HomefrontPayoutTable.TryGetTableAmount(kind, inSite, atUtc);
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? expected = HomefrontPayoutTable.TryGetExpected(
            kind, true, inSite, decision?.Outcome, decision?.CompletedWaveCount, atUtc);

        OutcomeText = (isAar, decision?.Outcome, decision?.CompletedWaveCount) switch
        {
            (true, _, { } waves) => $"{waves} of 9 waves paid",
            (true, _, null) => "not decided",
            (false, HomefrontOutcome.Completed, _) when decision?.OutcomeFromGameLog == true => "completed · from the game log",
            (false, HomefrontOutcome.Completed, _) => "completed",
            (false, HomefrontOutcome.Failed, _) => "failed",
            (false, HomefrontOutcome.Unknown, _) => "unknown",
            _ => "not decided"
        };
        string? storedVersion = detail.Runs.Select(run => run.HomefrontPayoutTableVersion).FirstOrDefault(v => v is not null);
        DateTime? effectiveFromUtc = table?.EffectiveFromUtc ?? expected?.EffectiveFromUtc;
        PayoutTableVersionText = kind is not null && storedVersion is not null && effectiveFromUtc is { } from
            && HomefrontPayoutTable.CurveFor(kind) is { } curve
            ? $"{kind} · {_CurveLabel(curve)} table, valid from {from:d MMM yyyy}"
            : null;
        PayoutSummaryText = (isAar, table, expected) switch
        {
            (true, _, { } aar) => $"{IskFormat.Whole(aar.Amount)} so far",
            (false, _, { } completed) => $"{IskFormat.Whole(completed.Amount)} each",
            (false, { } t, null) => $"if completed: {IskFormat.Whole(t.Amount)} each",
            _ => string.Empty
        };

        foreach (AttendanceRowViewModel row in rows)
        {
            decimal? confirmed = detail.Runs.FirstOrDefault(run => run.CharacterId == row.CharacterId) is { } ownRun
                ? detail.Parameters
                    .Where(parameter => parameter.RunId == ownRun.RunId && parameter.ParameterKey == RunParameterKey.FixedPayout)
                    .Select(parameter => parameter.Amount)
                    .FirstOrDefault()
                : null;
            row.ShowPayout(row.IsInSite ? table?.Amount : null, row.IsInSite ? expected?.Amount : null, confirmed);
        }
    }

    private static string _CurveLabel(HomefrontCurve curve) => curve switch
    {
        HomefrontCurve.FivePerson => "5-pilot",
        HomefrontCurve.ThreePerson => "3-pilot",
        HomefrontCurve.Metaliminal => "Metaliminal",
        HomefrontCurve.Aar => "AAR",
        _ => "unknown"
    };

    /// <summary>The list as decided; while editing an undecided activity, this client's own characters to start from.</summary>
    private static IEnumerable<RunAttendanceEntryInput> _EntriesToShow(ActivityDetailDto detail, RunAttendanceDecision? decision,
        IReadOnlySet<long> own) =>
        decision?.Entries ?? detail.Runs
            .Where(run => own.Contains(run.CharacterId))
            .Select(run => new RunAttendanceEntryInput
            {
                CharacterId = run.CharacterId,
                CharacterName = run.CharacterNameSnapshot,
                IsInSite = false,
                Reason = AttendanceReason.NoActivityLogged
            });

    private int? _CommanderOf(long? fleetId) =>
        fleetId is { } id && services.Services?.GetService<IFleetParticipation>() is { } participation
            ? participation.Current.FirstOrDefault(participant => participant.FleetId == id).FleetCommanderCharacterId
            : null;

    private static string _NameOf(RunAttendanceEntryInput entry, RunDetailSectionInput input) =>
        entry.CharacterName is { Length: > 0 } name ? name : input.NameOf(entry.CharacterId);

    private static string _DeciderName(RunAttendanceDecision decision, RunDetailSectionInput input) =>
        decision.Entries.FirstOrDefault(entry => entry.CharacterId == decision.SetByCharacterId)?.CharacterName
        ?? input.NameOf(decision.SetByCharacterId);
}
