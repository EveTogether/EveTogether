using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
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
/// HOMEFRONT on the detail screen (ET-230): how the site ended and who was in it at completion, the same one switch
/// and one line per character as the run window (ET-271). A saved homefront is corrected the same way it was flown —
/// one click on the outcome, on a row's tick or on a row's amount — and every click is written at once: to this
/// client's own runs, to the fleet again as <c>fleet.run-attendance</c>, and into the stored total the overview and the
/// month bar read (ET-271's rebuild after SAVE).
///
/// A homefront nobody decided — one saved before the outcome had a default — is shown undecided, with this client's
/// own characters in the site, and nothing is written until somebody clicks: an old homefront is never completed by a
/// guess (Jithran, 2026-09-12).
/// </summary>
public sealed partial class HomefrontDetailSectionViewModel(RunDetailSectionServices services)
    : RunDetailSection(RunSectionId.Homefront, "HOMEFRONT")
{
    private RunDetailSectionInput? _input;

    public ObservableCollection<AttendanceRowViewModel> Rows { get; } = [];

    /// <summary>This client decided the list — the fleet's commander, or the pilot over their own runs — or nobody
    /// did yet and every run here is its own, so it may set it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetOutcomeCommand))]
    [NotifyCanExecuteChangedFor(nameof(IncreaseNotOnRosterCommand))]
    [NotifyCanExecuteChangedFor(nameof(DecreaseNotOnRosterCommand))]
    [NotifyCanExecuteChangedFor(nameof(IncreaseWaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DecreaseWaveCommand))]
    [NotifyPropertyChangedFor(nameof(CanDecideOutcome))]
    private bool _canEdit;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DecreaseNotOnRosterCommand))]
    private int _notOnRosterCount;

    [ObservableProperty] private string? _editError;

    /// <summary>Why this list cannot be changed here, in one line — null when it can.</summary>
    [ObservableProperty] private string? _noticeText;

    // ── The outcome and the payout (ET-231, ET-271) ─────────────────────────────────────────────────

    /// <summary>"completed", "failed", "unknown", "not decided", or for Abyssal Artifact Recovery "N of 9 waves paid".</summary>
    [ObservableProperty] private string _outcomeText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsUnknown))]
    private HomefrontOutcome? _outcome;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IncreaseWaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DecreaseWaveCommand))]
    private int _completedWaveCount;

    public bool IsCompleted => Outcome is HomefrontOutcome.Completed;

    public bool IsFailed => Outcome is HomefrontOutcome.Failed;

    public bool IsUnknown => Outcome is HomefrontOutcome.Unknown;

    /// <summary>"Raid · 5-pilot table, valid from 19 Mar 2026" — which <see cref="HomefrontPayoutTable"/> entry this
    /// activity's own runs were priced against, read straight off the stored column
    /// (<c>Run.HomefrontPayoutTableVersion</c>) rather than recomputed here, so two clients on two app versions
    /// never silently disagree about the same site. Behind the summary line, not a line of its own.</summary>
    [ObservableProperty] private string? _payoutTableVersionText;

    /// <summary>"Completed · 5 in site · 15,000,000 each · 75,000,000 ISK total" — the same line the run window shows.</summary>
    [ObservableProperty] private string _payoutSummaryText = string.Empty;

    /// <summary>Whether the outcome can be set from this screen (ET-269) — the same permission as <see cref="CanEdit"/>.</summary>
    public bool CanDecideOutcome => CanEdit;

    public bool IsAar => _input?.RunType.HomefrontKind == "Abyssal Artifact Recovery";

    /// <summary>Drawn for every homefront, decided or not — the undecided one is exactly the one a click has to be
    /// able to complete.</summary>
    public override bool HasContent => _input?.RunType.PaysPerCharacterInSite == true;

    [RelayCommand(CanExecute = nameof(CanDecideOutcome))]
    private Task SetOutcomeAsync(HomefrontOutcome outcome) => _WriteAsync(before => (outcome, before?.CompletedWaveCount));

    [RelayCommand(CanExecute = nameof(_CanIncreaseWave))]
    private Task IncreaseWaveAsync() => _WriteWavesAsync(CompletedWaveCount + 1);

    private bool _CanIncreaseWave() => CanEdit && CompletedWaveCount < HomefrontWindowSectionViewModel.AllAarWaves;

    [RelayCommand(CanExecute = nameof(_CanDecreaseWave))]
    private Task DecreaseWaveAsync() => _WriteWavesAsync(CompletedWaveCount - 1);

    private bool _CanDecreaseWave() => CanEdit && CompletedWaveCount > 0;

    private Task _WriteWavesAsync(int waves) => _WriteAsync(before => (before?.Outcome, waves));

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task IncreaseNotOnRosterAsync()
    {
        NotOnRosterCount++;
        return _WriteAsync(before => (before?.Outcome, before?.CompletedWaveCount));
    }

    [RelayCommand(CanExecute = nameof(_CanDecreaseNotOnRoster))]
    private Task DecreaseNotOnRosterAsync()
    {
        NotOnRosterCount--;
        return _WriteAsync(before => (before?.Outcome, before?.CompletedWaveCount));
    }

    private bool _CanDecreaseNotOnRoster() => CanEdit && NotOnRosterCount > 0;

    public override void Apply(RunDetailSectionInput input)
    {
        _input = input;
        _Show();
    }

    // A row's tick is written the moment it changes (ET-271): there is no separate "change who was in site" step.
    private void _OnTicked(AttendanceRowViewModel row) =>
        _ = _WriteAsync(before => (before?.Outcome, before?.CompletedWaveCount));

    private void _OnEnterPayout(AttendanceRowViewModel row, decimal? amount) => _ = _SetPayoutAsync(row, amount);

    private async Task _SetPayoutAsync(AttendanceRowViewModel row, decimal? amount)
    {
        if (_input is not { } input
            || input.Detail.Runs.FirstOrDefault(run => run.CharacterId == row.CharacterId) is not { } ownRun)
            return;

        Result written = await services.Dispatcher.Send(new SetHomefrontPayoutCommand(ownRun.RunId, amount));
        if (written.IsSuccess)
            RaiseActivityCorrected();
        else
            EditError = written.Messages.FirstOrDefault()?.Text ?? "The amount could not be saved.";
    }

    /// <summary>The list as the rows show it now, with the outcome the caller picks, written onto this client's own
    /// runs and — as fleet commander — sent to every member. A line left as it was keeps its reason; one changed here
    /// was set by hand.</summary>
    private async Task _WriteAsync(Func<RunAttendanceDecision?, (HomefrontOutcome? Outcome, int? Waves)> outcomeOf)
    {
        if (_input is not { } input || !CanEdit)
            return;

        ActivityDetailDto detail = input.Detail;
        RunAttendanceDecision? before = detail.Attendance;
        IReadOnlySet<long> own = services.OwnCharacterIds ?? new HashSet<long>(detail.Runs.Select(run => run.CharacterId));
        int? commander = _CommanderOf(detail.FleetId);
        (AttendanceSource source, long setBy) = _ResolveDecider(before, commander, own, detail);
        (HomefrontOutcome? outcome, int? waves) = outcomeOf(before);
        bool isSameOutcome = outcome == before?.Outcome && waves == before?.CompletedWaveCount;

        RunAttendanceDecision decision = new(
            [.. Rows.Select(row => _EntryFor(row, before))], NotOnRosterCount, source, setBy, _NextSetAtUtc(before),
            IsAar ? null : outcome, IsAar ? waves : null,
            // Only the pale shadow line itself says "from the game log" (ET-262); a click replaces it.
            isSameOutcome && (before?.OutcomeFromGameLog ?? false));

        Result<int> written = await services.Dispatcher.Send(new SetRunAttendanceCommand(decision, [.. own],
            detail.GroupCode, detail.GroupCode is null ? detail.Runs.FirstOrDefault()?.RunId : null));
        if (!written.IsSuccess)
        {
            EditError = written.Messages.FirstOrDefault()?.Text ?? "The change could not be saved.";
            return;
        }

        EditError = null;
        if (decision.Source is AttendanceSource.FleetCommander && commander is { } fleetCommander
            && detail.FleetId is { } fleetId && detail.GroupCode is { } groupCode
            && services.Services?.GetService<IEventBus>() is { } eventBus)
            await eventBus.PublishAsync(new FleetRunAttendanceEvent(new RunGroupAttendance(
                    fleetId, groupCode, RunGroupAttendance.ToUnixMs(decision.SetAtUtc), decision.Entries,
                    decision.NotOnRosterCount, decision.Outcome, decision.CompletedWaveCount, decision.OutcomeFromGameLog),
                    fleetCommander),
                EventTarget.Remote);

        RaiseActivityCorrected();
    }

    /// <summary>Who decides this activity's attendance and outcome: the fleet commander once one is known, or
    /// otherwise whoever decided it last, or (nobody yet) the first of this client's own characters in it.</summary>
    private static (AttendanceSource Source, long SetBy) _ResolveDecider(
        RunAttendanceDecision? before, int? commander, IReadOnlySet<long> own, ActivityDetailDto detail)
    {
        bool asCommander = before?.Source is AttendanceSource.FleetCommander || commander is not null;
        long setBy = asCommander
            ? commander ?? before?.SetByCharacterId ?? 0
            : before?.SetByCharacterId ?? own.FirstOrDefault(id => detail.Runs.Any(run => run.CharacterId == id));
        return (asCommander ? AttendanceSource.FleetCommander : AttendanceSource.Pilot, setBy);
    }

    // Never stamped at or before the list it corrects: a receiver keeps the newest, and must take this one.
    private static DateTime _NextSetAtUtc(RunAttendanceDecision? before)
    {
        DateTime nowUtc = DateTime.UtcNow;
        return RunGroupAttendance.ToWireInstant(
            before is { } previous && previous.SetAtUtc >= nowUtc ? previous.SetAtUtc.AddMilliseconds(1) : nowUtc);
    }

    private static RunAttendanceEntryInput _EntryFor(AttendanceRowViewModel row, RunAttendanceDecision? before) =>
        before?.Entries.FirstOrDefault(entry => entry.CharacterId == row.CharacterId) is { } kept && kept.IsInSite == row.IsInSite
            ? kept
            : new RunAttendanceEntryInput
            {
                CharacterId = row.CharacterId,
                CharacterName = row.Name,
                IsInSite = row.IsInSite,
                IsExternal = row.IsExternal,
                // Nothing was decided before: the undecided list is this client's own characters in the site, as
                // proposed, not a tick anybody set.
                Reason = before is null ? AttendanceReason.NoActivityLogged : AttendanceReason.SetByHand
            };

    private void _Show()
    {
        if (_input is not { } input)
            return;

        ActivityDetailDto detail = input.Detail;
        RunAttendanceDecision? decision = detail.Attendance;
        IReadOnlySet<long> own = services.OwnCharacterIds ?? new HashSet<long>(detail.Runs.Select(run => run.CharacterId));
        int? commander = _CommanderOf(detail.FleetId);
        CanEdit = (decision, commander) switch
        {
            ({ } decided, _) => own.Contains(commander ?? decided.SetByCharacterId),
            (null, { } fleetCommander) => own.Contains(fleetCommander),
            // Nobody decided and no commander can be named: this client may settle it when every run here is its own.
            _ => detail.Runs.Count > 0 && detail.Runs.All(run => own.Contains(run.CharacterId))
        };

        NotOnRosterCount = decision?.NotOnRosterCount ?? 0;
        Outcome = decision?.Outcome;
        CompletedWaveCount = decision?.CompletedWaveCount ?? 0;
        NoticeText = (CanEdit, decision) switch
        {
            (true, _) => null,
            (false, { } decided) => $"Set by {_DeciderName(decided, input)} at {decided.SetAtUtc.ToLocalTime():HH:mm} — ask them to change it.",
            _ => "Nobody decided who was in the site."
        };

        List<AttendanceRowViewModel> rows = [];
        foreach (RunAttendanceEntryInput entry in _EntriesToShow(detail, decision, own))
        {
            bool isLocal = own.Contains(entry.CharacterId);
            AttendanceRowViewModel row = new(entry.CharacterId, _NameOf(entry, input), isLocal, entry.IsExternal,
                _OnTicked, _OnEnterPayout);
            row.Show(entry.IsInSite, entry.Reason, entry.ReasonAmount, false,
                entry.Reason is AttendanceReason.SetByHand && decision is not null && !CanEdit ? _DeciderName(decision, input) : null);
            row.IsEditable = CanEdit;
            row.HasRun = detail.Runs.Any(run => run.CharacterId == entry.CharacterId);
            rows.Add(row);
        }

        Rows.Clear();
        foreach (AttendanceRowViewModel row in rows
                     .OrderByDescending(row => row.IsLocal)
                     .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase))
            Rows.Add(row);
        for (int i = 0; i < Rows.Count; i++)
            Rows[i].IsAlternate = i % 2 == 1;

        int inSite = rows.Count(row => row.IsInSite) + NotOnRosterCount;
        _ShowPayout(input, detail, decision, inSite);
        HeaderSummary = (IsAar, decision) switch
        {
            (_, null) => $"not decided · {inSite} in site",
            (true, _) => $"{OutcomeText} · {inSite} in site",
            _ => $"{OutcomeText} · {inSite} in site"
        };
    }

    /// <summary>The fixed payout at N (ET-231), on every Local row and in the summary line — read straight off
    /// <see cref="HomefrontPayoutTable"/>, never recomputed differently on two screens.</summary>
    private void _ShowPayout(RunDetailSectionInput input, ActivityDetailDto detail, RunAttendanceDecision? decision, int inSite)
    {
        string? kind = input.RunType.HomefrontKind;
        DateTime atUtc = detail.StoppedAtUtc ?? DateTime.UtcNow;
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? table = HomefrontPayoutTable.TryGetTableAmount(kind, inSite, atUtc);
        (decimal Amount, string Version, DateTime EffectiveFromUtc)? owed = HomefrontPayoutTable.TryGetExpected(
            kind, true, inSite, decision?.Outcome, decision?.CompletedWaveCount, atUtc);

        OutcomeText = (IsAar, decision?.Outcome, decision?.CompletedWaveCount) switch
        {
            (true, _, { } waves) => $"{waves} of 9 waves paid",
            (false, HomefrontOutcome.Completed, _) when decision?.OutcomeFromGameLog == true => "completed · from the game log",
            (false, HomefrontOutcome.Completed, _) => "completed",
            (false, HomefrontOutcome.Failed, _) => "failed",
            (false, HomefrontOutcome.Unknown, _) => "unknown",
            _ => "not decided"
        };
        string? storedVersion = detail.Runs.Select(run => run.HomefrontPayoutTableVersion).FirstOrDefault(v => v is not null);
        DateTime? effectiveFromUtc = table?.EffectiveFromUtc ?? owed?.EffectiveFromUtc;
        PayoutTableVersionText = kind is not null && storedVersion is not null && effectiveFromUtc is { } from
            && HomefrontPayoutTable.CurveFor(kind) is { } curve
            ? $"{kind} · {_CurveLabel(curve)} table, valid from {from:d MMM yyyy}"
            : null;

        foreach (AttendanceRowViewModel row in Rows)
        {
            decimal? typed = detail.Runs.FirstOrDefault(run => run.CharacterId == row.CharacterId) is { } ownRun
                ? detail.Parameters
                    .Where(parameter => parameter.RunId == ownRun.RunId && parameter.ParameterKey == RunParameterKey.FixedPayout)
                    .Select(parameter => parameter.Amount)
                    .FirstOrDefault()
                : null;
            row.ShowPayout(row.IsInSite ? table?.Amount ?? owed?.Amount : null, row.IsInSite ? owed?.Amount : null,
                row.IsInSite ? typed : null);
        }

        PayoutSummaryText = HomefrontPayoutSummary.Describe(IsAar, decision?.Outcome, decision?.CompletedWaveCount ?? 0,
            inSite, table?.Amount, owed?.Amount, Rows);
    }

    private static string _CurveLabel(HomefrontCurve curve) => curve switch
    {
        HomefrontCurve.FivePerson => "5-pilot",
        HomefrontCurve.ThreePerson => "3-pilot",
        HomefrontCurve.Metaliminal => "Metaliminal",
        HomefrontCurve.Aar => "AAR",
        _ => "unknown"
    };

    /// <summary>The list as decided; for an undecided activity, this client's own characters to start from —
    /// deliberately put into the run, so in the site until somebody says otherwise (ET-269), same as the run window.</summary>
    private static IEnumerable<RunAttendanceEntryInput> _EntriesToShow(ActivityDetailDto detail, RunAttendanceDecision? decision,
        IReadOnlySet<long> own) =>
        decision?.Entries ?? detail.Runs
            .Where(run => own.Contains(run.CharacterId))
            .DistinctBy(run => run.CharacterId)
            .Select(run => new RunAttendanceEntryInput
            {
                CharacterId = run.CharacterId,
                CharacterName = run.CharacterNameSnapshot,
                IsInSite = true,
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
