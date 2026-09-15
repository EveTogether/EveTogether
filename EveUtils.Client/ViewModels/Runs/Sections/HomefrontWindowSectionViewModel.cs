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
using EveUtils.Client.Gamelog;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs.Attendance;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// HOMEFRONT in the run window (ET-230): how the site ended and who was in it when it completed — the two facts a
/// homefront pays per character on — as one flat list of every character on the fleet's roster, this client's own
/// ones tagged "Local".
///
/// The normal case costs nothing (Jithran, 2026-09-12: "ga er standaard vanuit dat je de site optimaal draait"): the
/// outcome starts at Completed, everyone in the fleet starts in the site, and the list is written as it stands, so STOP
/// is the last thing anyone has to do. An exception costs one click — the outcome, a row's tick, a row's amount (ET-271).
///
/// The app proposes (<see cref="AttendanceProposal"/>); one person decides. In a fleet that is whoever commands it,
/// and their list goes to every member as <c>fleet.run-attendance</c>, where each client writes it onto its own runs
/// (<see cref="FleetRunAttendance"/>) and shows it read-only. Without a fleet the pilot decides for their own runs.
/// A member's window never writes a list of its own: it shows the commander's, or says it is waiting for it.
///
/// A click is written the moment it is made and a run's first list at once; only what the window works out by itself
/// — evidence, a roster read — waits the short bundle window the fleet share waits (ET-242), and never over a list
/// decided elsewhere since (I1, I5 on <see cref="SetRunAttendanceCommand"/>). The commander's window sends the list
/// again every half minute for a member who connected late.
/// </summary>
public sealed partial class HomefrontWindowSectionViewModel : RunWindowSection
{
    /// <summary>Ticks come in bursts — a commander running down the list — so a change waits this long and goes out
    /// once.</summary>
    public static readonly TimeSpan BundleWindow = TimeSpan.FromSeconds(2);

    /// <summary>The commander's list goes out again this often while the window is open, for a member whose client
    /// connected after the last change — the server keeps no fleet message for anyone.</summary>
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(30);

    /// <summary>What an Abyssal Artifact Recovery site pays for when it goes the way it normally does: all of it.</summary>
    public const int AllAarWaves = HomefrontCatalogue.AarWaveCount;

    private static readonly TimeSpan RosterReadInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StoredReadInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly AttendanceEvidenceCollector _evidence = new();
    private readonly Dictionary<long, bool> _overrides = [];
    private readonly Dictionary<long, string> _names = [];
    private readonly HashSet<long> _namesAsked = [];
    private readonly Dictionary<long, (PresenceState State, DateTimeOffset HeardAt)> _heard = [];
    // What this window typed over the table's figure, per character — null for a correction dropped again. Ahead of
    // Participants' own FixedPayoutIsk until that asynchronous mirror catches up with the write (ET-269's rule).
    private readonly Dictionary<long, decimal?> _corrections = [];
    private readonly GamelogClientService? _gamelog;
    private readonly FleetRunAttendance? _attendance;
    private readonly IDisposable? _metricSubscription;
    private readonly IDisposable? _runsSubscription;

    private IReadOnlySet<long> _own = new HashSet<long>();
    private bool _isOwnLoaded;
    private bool _isLoadingOwn;
    private IReadOnlyList<RosterCharacter>? _roster;
    private long? _rosterFleetId;
    private DateTime? _rosterReadAtUtc;
    private bool _isReadingRoster;
    private RunAttendanceDecision? _lastSite;
    private string? _lastSiteGroupCode;
    private RunAttendanceDecision? _stored;
    private RunAttendanceDecision? _standing;
    private bool _isStandingTaken;
    private DateTime? _storedReadAtUtc;
    private volatile bool _isStoredStale = true;
    private bool _isReadingStored;
    private DateTime? _changedSinceUtc;
    private DateTime? _sentAtUtc;
    private DateTime _lastSetAtUtc;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _isClosed;
    // Which run everything below belongs to — the group code, or the run itself when it has none. A window that goes on
    // to the next site starts its list, outcome and hand ticks over rather than carrying the last site's along.
    private string? _stateKey;
    // Something a person did in this window that has not been written yet — the one thing still written once the run
    // is saved, so a click after SAVE is kept while a roster that drifts after SAVE never rewrites the list by itself.
    private bool _isChangedByHand;
    // Somebody picked the outcome (or AAR's waves) in this window: no default ever goes over it.
    private bool _isOutcomeSetByHand;
    // When the list this window last took up or wrote was set. A stored list newer than this was decided somewhere else
    // — another window, the detail screen, the commander — and is taken up rather than argued with (I5).
    private DateTime _knownSetAtUtc;
    private int? _fleetSizeAtStop;
    private DateTime _nowUtc = DateTime.UtcNow;

    // The Metaliminal Meteoroid "pale shadow" line (ET-262), guarded like _heard: the gamelog watcher's pump thread
    // writes it, this window's own tick reads it. Latched rather than cleared once applied — a site has one asteroid,
    // so a second sighting (a different mining module's own copy of the line, or a stray re-fire) only ever confirms
    // what already stands.
    private (int CharacterId, DateTime AtUtc)? _paleShadow;

    public HomefrontWindowSectionViewModel(IRunWindowContext context)
        : base(context, RunSectionId.Homefront, "HOMEFRONT")
    {
        IsExpanded = true;
        _gamelog = context.Services.GetService<GamelogClientService>();
        if (_gamelog is not null)
        {
            _gamelog.ContributionObserved += _OnContribution;
            _gamelog.HomefrontCompletionObserved += _OnHomefrontCompletion;
        }
        _attendance = context.Services.GetService<FleetRunAttendance>();
        if (_attendance is not null)
            _attendance.Applied += _OnAttendanceApplied;
        _metricSubscription = context.Services.GetService<IEventBus>()?.Subscribe<FleetMetricEvent>(_OnFleetMetric);
        // A change made anywhere else — the detail screen, another window — is read on the very next tick rather than
        // at the next five-second read, so this window never shows a total the store has already moved on from (I2).
        _runsSubscription = context.Services.GetService<IEventBus>()?.Subscribe<RunsChangedEvent>(_OnRunsChanged);
    }

    /// <summary>One flat list: this client's own characters first, the rest A–Z. Never grouped by player — which
    /// characters belong to one player is not known here (Jithran, 2026-09-11).</summary>
    public ObservableCollection<AttendanceRowViewModel> Rows { get; } = [];

    /// <summary>This client decides the list: it commands the fleet, or flies without one.</summary>
    [ObservableProperty] private bool _canDecide;

    /// <summary>"5 in site" — N, what the payout table is read at.</summary>
    [ObservableProperty] private string _countText = string.Empty;

    /// <summary>Pilots in the site who are on no roster at all — a stranger who joined in. They count in N.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DecreaseNotOnRosterCommand))]
    private int _notOnRosterCount;

    /// <summary>The one line that says why this list cannot be changed here, or what it waits on — null in the normal
    /// case, where there is nothing to say.</summary>
    [ObservableProperty] private string? _noticeText;

    // ── The outcome and the payout (ET-231, ET-271) ─────────────────────────────────────────────────

    /// <summary>Abyssal Artifact Recovery has no completed/failed/unknown of its own — it pays per wave, and a site
    /// that fails part-way keeps whatever waves it already cleared. Every other homefront picks an outcome instead.</summary>
    public bool IsAar => Context.RunType.HomefrontKind == "Abyssal Artifact Recovery";

    /// <summary>How the site ended. Completed from the start of a new run (ET-271) — null only on a run whose list was
    /// written before that, which nobody's guess fills in. Editable only while <see cref="CanDecide"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutcomeText))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsUnknown))]
    private HomefrontOutcome? _outcome;

    /// <summary>Whether <see cref="Outcome"/> came from the Metaliminal Meteoroid "pale shadow" gamelog line (ET-262)
    /// rather than a manual pick — cleared the moment <see cref="SetOutcome"/> is used, even to the same value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutcomeText))]
    private bool _outcomeIsFromGameLog;

    /// <summary>AAR's own outcome: how many of its 9 waves paid out.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DecreaseWaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(IncreaseWaveCommand))]
    private int _completedWaveCount;

    /// <summary>"Completed · 5 in site · 15,000,000 ISK each · 75,000,000 ISK total" — what the site pays, read
    /// straight off <see cref="HomefrontPayoutTable"/> and the rows' own figures.</summary>
    [ObservableProperty] private string _payoutSummaryText = string.Empty;

    /// <summary>The three outcomes are one switch (ET-271): exactly one of these lights, and clicking another moves it.</summary>
    public bool IsCompleted => Outcome is HomefrontOutcome.Completed;

    public bool IsFailed => Outcome is HomefrontOutcome.Failed;

    public bool IsUnknown => Outcome is HomefrontOutcome.Unknown;

    /// <summary>"completed", "failed" or "unknown" — <see cref="Outcome"/> in words, for a reader who cannot change it.</summary>
    public string OutcomeText => Outcome switch
    {
        HomefrontOutcome.Completed when OutcomeIsFromGameLog => "completed · from the game log",
        HomefrontOutcome.Completed => "completed",
        HomefrontOutcome.Failed => "failed",
        HomefrontOutcome.Unknown => "unknown",
        _ => "not decided"
    };

    [RelayCommand]
    private void SetOutcome(HomefrontOutcome outcome)
    {
        if (!CanDecide)
            return;

        Outcome = outcome;
        OutcomeIsFromGameLog = false;
        _isOutcomeSetByHand = true;
        _NoteChangeByHand();
    }

    [RelayCommand(CanExecute = nameof(_CanIncreaseWave))]
    private void IncreaseWave() => _SetWaveCount(CompletedWaveCount + 1);

    private bool _CanIncreaseWave() => CompletedWaveCount < AllAarWaves;

    [RelayCommand(CanExecute = nameof(_CanDecreaseWave))]
    private void DecreaseWave() => _SetWaveCount(CompletedWaveCount - 1);

    private bool _CanDecreaseWave() => CompletedWaveCount > 0;

    private void _SetWaveCount(int count)
    {
        if (!CanDecide || count is < 0 or > AllAarWaves)
            return;

        CompletedWaveCount = count;
        _isOutcomeSetByHand = true;
        _NoteChangeByHand();
    }

    /// <summary>A click is stored the moment it is made (I1, SetRunAttendanceCommand): never held here until a bundle
    /// window, a tick or a SAVE comes round — the window where HF-7TQB's Completed stood in the header and nowhere
    /// else.</summary>
    // Counts clicks, so a write running off the UI thread (ET-287) knows whether one landed while it was under way — the
    // list it wrote was worked out before that click, and must not mark the click as written.
    private int _handChangeCount;

    private void _NoteChangeByHand()
    {
        _handChangeCount++;
        _isChangedByHand = true;
        _changedSinceUtc ??= _nowUtc;
        _Rebuild(_nowUtc);
        _ = _WriteIfDueAsync(_nowUtc, isForced: true);
    }

    /// <summary>What the pilot typed over the table's figure for <paramref name="characterId"/>, or null when they
    /// typed none — the one figure the run window's TOTAL ISK counts in place of the table's.</summary>
    public decimal? CorrectionOf(long characterId)
    {
        decimal? stored = Context.Participants.FirstOrDefault(participant => participant.CharacterId == characterId)?.FixedPayoutIsk;
        if (!_corrections.TryGetValue(characterId, out decimal? typed))
            return stored;
        // Only a bridge until the store's own copy shows it: from then on the store is the one source, so a figure
        // changed on the detail screen afterwards reaches this window too.
        if (typed == stored)
            _corrections.Remove(characterId);
        return typed;
    }

    public override void Refresh(DateTime nowUtc)
    {
        _nowUtc = nowUtc;
        _StartOverIfAnotherRun();
        _evidence.SetWindow(Context.EffectiveStartUtc, Context.EffectiveStopUtc);
        foreach (RunParticipantViewModel participant in Context.Participants)
            _evidence.SetMined(participant.CharacterId, participant.MiningEntries.Sum(entry => entry.Units));
        _NoteSharedMining();

        _ = _LoadOwnAsync();
        _ = _ReadRosterIfDueAsync(nowUtc);
        _ = _ReadStoredIfDueAsync(nowUtc);
        _ = _ReadLastSiteIfDueAsync();
        _Rebuild(nowUtc);
        _ = _WriteIfDueAsync(nowUtc);
    }

    public override void RefreshSummary() => HeaderSummary = (IsAar, Outcome) switch
    {
        (true, _) => $"{CompletedWaveCount} of {AllAarWaves} waves · {CountText}",
        (false, { }) => $"{OutcomeText} · {CountText}",
        _ => CountText
    };

    public override void OnRunStarted()
    {
        _isClosed = false;
        _isStoredStale = true;
        lock (_gate)
            _paleShadow = null;
    }

    public override void OnRunClosed() => _isClosed = true;

    /// <summary>STOP records the outcome and the list as they stand (ET-271): written now, on every run of the group,
    /// without waiting out the bundle window — SAVE commits the rows and adds up the total straight after this.</summary>
    public override async Task BeforeSaveAsync()
    {
        DateTime nowUtc = DateTime.UtcNow;
        await _LoadOwnAsync();
        if (_storedReadAtUtc is null)
            await _ReadStoredIfDueAsync(nowUtc);
        _Rebuild(nowUtc);
        await _WriteIfDueAsync(nowUtc, isForced: true);
    }

    /// <summary>The fleet's size at STOP goes onto every one of this client's runs in the group — a snapshot beside N,
    /// never N itself.</summary>
    public override void AddToSave(RunSaveDraft draft) =>
        draft.FleetSizeAtStop = _fleetSizeAtStop ?? (Context.FleetId is not null ? _roster?.Count : null);

    protected override void OnContextChanged(string? propertyName)
    {
        if (propertyName == nameof(IRunWindowContext.RunState) && Context.RunState is ActivityRunState.Stopped
            && Context.FleetId is not null && _roster is not null)
            _fleetSizeAtStop = _roster.Count;
        else if (propertyName is nameof(IRunWindowContext.GroupCode) or nameof(IRunWindowContext.RunId))
            _isStoredStale = true;
    }

    public override void Dispose()
    {
        if (_gamelog is not null)
        {
            _gamelog.ContributionObserved -= _OnContribution;
            _gamelog.HomefrontCompletionObserved -= _OnHomefrontCompletion;
        }
        if (_attendance is not null)
            _attendance.Applied -= _OnAttendanceApplied;
        _metricSubscription?.Dispose();
        _runsSubscription?.Dispose();
        base.Dispose();
    }

    [RelayCommand]
    private void IncreaseNotOnRoster() => _SetNotOnRoster(NotOnRosterCount + 1);

    [RelayCommand(CanExecute = nameof(CanDecreaseNotOnRoster))]
    private void DecreaseNotOnRoster() => _SetNotOnRoster(NotOnRosterCount - 1);

    private bool CanDecreaseNotOnRoster() => NotOnRosterCount > 0;

    private void _SetNotOnRoster(int count)
    {
        if (!CanDecide || count < 0)
            return;

        NotOnRosterCount = count;
        _NoteChangeByHand();
    }

    // ── Who decides ─────────────────────────────────────────────────────────────────────────────────

    private enum Role
    {
        /// <summary>A run of the pilot's own — solo, or their own toons with no fleet.</summary>
        Pilot,

        Commander,
        Member,

        /// <summary>A fleet whose commander the roster cannot name right now. Not knowing is never "you decide":
        /// the list reaches other people's runs.</summary>
        Unknown
    }

    private int? _CommanderId() => Context.FleetId is { } fleetId
        ? Context.Services.GetService<IFleetParticipation>()?.Current
            .FirstOrDefault(participant => participant.FleetId == fleetId).FleetCommanderCharacterId
        : null;

    /// <summary>The list as it stands was written by one of this client's own characters — so this client decides it,
    /// even while the fleet read blinks. Without this a commander whose fleet membership was read empty for one tick
    /// became a mere pilot, whose writes the store refuses over a commander's list: the Completed clicked in the run
    /// window stood in the header and never reached a single run (HF-7TQB, 2026-09-12).</summary>
    private bool _IsOwnDecision => _stored is { } stored && _own.Contains(stored.SetByCharacterId);

    private Role _RoleNow()
    {
        int? commander = Context.GroupCode is not null ? _CommanderId() : null;
        if (commander is { } named)
            // A commander the roster names outranks any list this client wrote before — unless it is this client's own.
            return _own.Contains(named) ? Role.Commander : Role.Member;
        if (_IsOwnDecision)
            return _stored?.Source is AttendanceSource.FleetCommander ? Role.Commander : Role.Pilot;

        return Context.GroupCode is null || Context.FleetId is null ? Role.Pilot : Role.Unknown;
    }

    /// <summary>Who signs a commander's list: the commander the roster names now, or — while it names nobody for a
    /// moment — whoever of this client's own characters signed it last.</summary>
    private long? _CommanderSigner() => _CommanderId() ?? (_IsOwnDecision ? _stored?.SetByCharacterId : null);

    // ── The list ────────────────────────────────────────────────────────────────────────────────────

    private void _Rebuild(DateTime nowUtc)
    {
        _StartOverIfAnotherRun();
        Role role = _RoleNow();
        CanDecide = role is Role.Pilot or Role.Commander;
        if (CanDecide && !_isStandingTaken && _storedReadAtUtc is not null)
        {
            // Picked up once, the first time this window decides with the store read: a reopened window or a new
            // commander goes on from the list as it stands instead of starting it over.
            _standing = _stored;
            _isStandingTaken = true;
            _knownSetAtUtc = _stored?.SetAtUtc ?? default;
            NotOnRosterCount = _stored?.NotOnRosterCount ?? NotOnRosterCount;
            // A pick made in this window before the store was read stands; the store fills in only what nobody picked.
            if (!_isOutcomeSetByHand)
            {
                Outcome = _stored?.Outcome ?? Outcome;
                OutcomeIsFromGameLog = _stored?.OutcomeFromGameLog ?? OutcomeIsFromGameLog;
                CompletedWaveCount = _stored?.CompletedWaveCount ?? CompletedWaveCount;
            }
            _DefaultOutcomeIfNew();
        }
        else if (!CanDecide)
            _isStandingTaken = false;

        _ApplyGameLogOutcomeIfDue(nowUtc);

        IReadOnlyList<AttendanceCandidate> candidates = _Candidates(role);
        string? commanderName = _CommanderId() is { } commander ? _NameOf(commander) : null;
        RunAttendanceDecision? shownDecision;

        if (CanDecide)
        {
            shownDecision = _CurrentDecision(candidates, role, nowUtc);
            _ShowDecision(candidates, shownDecision, commanderName: null);
        }
        else if (_stored is { } stored)
        {
            shownDecision = stored;
            _ShowDecision(candidates, stored, commanderName);
            Outcome = stored.Outcome;
            OutcomeIsFromGameLog = stored.OutcomeFromGameLog;
            CompletedWaveCount = stored.CompletedWaveCount ?? 0;
        }
        else
        {
            shownDecision = null;
            _ShowWaiting(candidates);
        }

        // The one thing _RefreshGroupTotalIsk reads for a homefront's payout (ET-269): the run window's own header
        // used to read Participants' own mirror of this instead, refreshed by a separate, asynchronous DB round trip
        // — so right after a manual SetOutcome the header alternated between this decision (drawn immediately, in
        // memory) and the stale mirror (not yet caught up), and kept doing so as long as both readers disagreed
        // about which was current. One property, written here, is the only decision the header may read.
        LiveDecision = shownDecision;

        _ShowPayout(shownDecision);
        _Describe(role, commanderName);
        RefreshSummary();
    }

    private static string? _KeyOf(IRunWindowContext context) => context.GroupCode ?? context.RunId?.ToString();

    /// <summary>The window went on to another run — the next site after SAVE, or another group adopted: nothing of the
    /// last one's list, outcome, hand ticks or typed amounts may carry over onto it.</summary>
    private void _StartOverIfAnotherRun()
    {
        string? key = _KeyOf(Context);
        if (key == _stateKey)
            return;

        _stateKey = key;
        _overrides.Clear();
        _corrections.Clear();
        _stored = null;
        _standing = null;
        _isStandingTaken = false;
        _storedReadAtUtc = null;
        _isStoredStale = true;
        _changedSinceUtc = null;
        _sentAtUtc = null;
        _isChangedByHand = false;
        _isOutcomeSetByHand = false;
        _fleetSizeAtStop = null;
        Outcome = null;
        OutcomeIsFromGameLog = false;
        CompletedWaveCount = 0;
        NotOnRosterCount = 0;
    }

    /// <summary>A run nobody has written a list for yet goes the way a site normally goes (ET-271): Completed, or for
    /// AAR every wave paid. A list written before — by an older version, or a pick somebody made — keeps what it says,
    /// "not decided" included: an old homefront is never completed by a guess.</summary>
    private void _DefaultOutcomeIfNew()
    {
        if (_stored is not null || _isOutcomeSetByHand
            || Context.RunState is not (ActivityRunState.Running or ActivityRunState.Stopped))
            return;

        if (IsAar)
            CompletedWaveCount = AllAarWaves;
        else
            Outcome = HomefrontOutcome.Completed;
    }

    /// <summary>The decision exactly as this tick shows it — this client's own live pick while it decides, the
    /// commander's stored list while it only reads one, or null while it is still waiting for either. The single
    /// source <see cref="ActivityWindowViewModel"/>'s TOTAL ISK reads a homefront's outcome and N from, so the header
    /// can never disagree with what HOMEFRONT itself is showing right now.</summary>
    public RunAttendanceDecision? LiveDecision { get; private set; }

    /// <summary>The fixed payout at the current N (ET-231), on every Local row and in the one summary line — read
    /// straight off <see cref="HomefrontPayoutTable"/>, a typed figure standing in for a row's own.</summary>
    private void _ShowPayout(RunAttendanceDecision? decision)
    {
        string? kind = Context.RunType.HomefrontKind;
        int? n = decision?.InSiteCount;
        DateTime atUtc = Context.EffectiveStopUtc ?? _nowUtc;
        decimal? table = HomefrontPayoutTable.TryGetTableAmount(kind, n, atUtc)?.Amount;
        decimal? expected = HomefrontPayoutTable.TryGetExpected(kind, true, n, Outcome, CompletedWaveCount, atUtc)?.Amount;

        foreach (AttendanceRowViewModel row in Rows)
        {
            row.HasRun = Context.Participants.Any(participant => participant.CharacterId == row.CharacterId);
            row.ShowPayout(row.IsInSite ? table ?? expected : null, row.IsInSite ? expected : null,
                row.IsInSite ? CorrectionOf(row.CharacterId) : null);
        }

        PayoutSummaryText = HomefrontPayoutSummary.Describe(IsAar, Outcome, CompletedWaveCount, n, table, expected, Rows);
    }

    private IReadOnlyList<AttendanceCandidate> _Candidates(Role role)
    {
        Dictionary<long, AttendanceCandidate> candidates = [];
        foreach (RunParticipantViewModel participant in Context.Participants.Where(p => _own.Contains(p.CharacterId)))
        {
            _names[participant.CharacterId] = participant.CharacterName;
            candidates[participant.CharacterId] = new AttendanceCandidate(participant.CharacterId, participant.CharacterName,
                IsLocal: true, IsExternal: false);
        }

        // An own character on the roster without a run here: in the site like everyone else, unless this client sees
        // them logged out — a character with a run was picked into it, and a flaky window title never takes it out.
        if (Context.FleetId is not null)
            foreach (RosterCharacter member in _roster ?? [])
            {
                bool isLocal = _own.Contains(member.CharacterId);
                candidates.TryAdd(member.CharacterId, new AttendanceCandidate(member.CharacterId, _NameOf(member.CharacterId),
                    isLocal, member.IsExternal,
                    IsLoggedOut: isLocal && AttendanceRoster.PresenceOf(Context.Services, member.CharacterId, isLocal: true,
                        null, null, new DateTimeOffset(_nowUtc, TimeSpan.Zero)) is FleetMemberPresenceState.Offline));
            }

        // A member shows the commander's whole list, whoever this client's own roster read left out.
        if (role is not (Role.Pilot or Role.Commander))
            foreach (RunAttendanceEntryInput entry in _stored?.Entries ?? [])
            {
                if (entry.CharacterName is { Length: > 0 } name)
                    _names.TryAdd(entry.CharacterId, name);
                candidates.TryAdd(entry.CharacterId, new AttendanceCandidate(entry.CharacterId, _NameOf(entry.CharacterId),
                    _own.Contains(entry.CharacterId), entry.IsExternal));
            }

        return [.. candidates.Values
            .OrderByDescending(candidate => candidate.IsLocal)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The list as the one who decides has it right now: the proposal, with whatever they set by hand over
    /// it.</summary>
    private RunAttendanceDecision _CurrentDecision(IReadOnlyList<AttendanceCandidate> candidates, Role role, DateTime nowUtc)
    {
        IReadOnlyList<AttendanceProposalLine> proposal = AttendanceProposal.Propose(
            candidates, id => _evidence.Best(id), _lastSite, _standing);
        List<RunAttendanceEntryInput> entries = [];
        foreach ((AttendanceCandidate candidate, AttendanceProposalLine line) in candidates.Zip(proposal))
        {
            bool isInSite = _overrides.TryGetValue(candidate.CharacterId, out bool set) ? set : line.IsInSite;
            entries.Add(new RunAttendanceEntryInput
            {
                CharacterId = candidate.CharacterId,
                // Only a name really known; a "Char 123" placeholder is not a name to store.
                CharacterName = _names.GetValueOrDefault(candidate.CharacterId),
                IsInSite = isInSite,
                IsExternal = candidate.IsExternal,
                Reason = isInSite == line.IsInSite ? line.Reason : AttendanceReason.SetByHand,
                ReasonAmount = isInSite == line.IsInSite ? line.Amount : null
            });
        }

        long setBy = role is Role.Commander && _CommanderSigner() is { } commander
            ? commander
            : _IsOwnDecision && _stored is { } own ? own.SetByCharacterId : Context.ActingCharacterId ?? Context.RunCharacterId ?? 0;
        return new RunAttendanceDecision(entries, NotOnRosterCount,
            role is Role.Commander ? AttendanceSource.FleetCommander : AttendanceSource.Pilot, setBy, nowUtc,
            IsAar ? null : Outcome, IsAar ? CompletedWaveCount : null, !IsAar && OutcomeIsFromGameLog);
    }

    private void _ShowDecision(IReadOnlyList<AttendanceCandidate> candidates, RunAttendanceDecision decision, string? commanderName)
    {
        IReadOnlyList<AttendanceProposalLine> proposal = CanDecide
            ? AttendanceProposal.Propose(candidates, id => _evidence.Best(id), _lastSite, _standing)
            : [];
        List<AttendanceRowViewModel> rows = [];
        foreach (AttendanceCandidate candidate in candidates)
        {
            AttendanceRowViewModel row = _RowFor(candidate);
            row.IsEditable = CanDecide;
            RunAttendanceEntryInput? entry = decision.Entries.FirstOrDefault(e => e.CharacterId == candidate.CharacterId);
            AttendanceProposalLine? line = proposal.FirstOrDefault(p => p.CharacterId == candidate.CharacterId);
            bool isByHand = entry?.Reason is AttendanceReason.SetByHand;
            AttendanceEvidence shown = _ShownReason(candidate, entry, line);
            row.Show(entry?.IsInSite ?? false, shown.Reason, shown.Amount, line?.IsAddedToLastSite == true && !isByHand,
                isByHand && !CanDecide ? commanderName ?? "the fleet commander" : null);
            row.Presence = _PresenceOf(candidate);
            rows.Add(row);
        }

        _Place(rows);
        _Count(decision.InSiteCount);
        if (!CanDecide)
            NotOnRosterCount = decision.NotOnRosterCount;
    }

    /// <summary>
    /// The reason beside a tick. A line set by hand still shows what the evidence said, so the one who decides sees
    /// why the proposal disagreed. A member's own characters show their own gamelog's evidence beside the commander's
    /// tick, which this client can see better than the commander can.
    /// </summary>
    private AttendanceEvidence _ShownReason(AttendanceCandidate candidate, RunAttendanceEntryInput? entry,
        AttendanceProposalLine? line)
    {
        AttendanceEvidence? ownEvidence = candidate.IsLocal ? _evidence.Best(candidate.CharacterId) : null;
        if (entry?.Reason is AttendanceReason.SetByHand)
            return line is { Reason: not AttendanceReason.SetByHand }
                ? new AttendanceEvidence(line.Reason, line.Amount)
                : ownEvidence ?? new AttendanceEvidence(AttendanceReason.NoActivityLogged, null);

        if (!CanDecide && ownEvidence is not null)
            return ownEvidence;

        return new AttendanceEvidence(entry?.Reason ?? AttendanceReason.NoActivityLogged, entry?.ReasonAmount);
    }

    private void _ShowWaiting(IReadOnlyList<AttendanceCandidate> candidates)
    {
        List<AttendanceRowViewModel> rows = [];
        foreach (AttendanceCandidate candidate in candidates)
        {
            AttendanceRowViewModel row = _RowFor(candidate);
            row.IsEditable = false;
            AttendanceEvidence? seen = candidate.IsLocal ? _evidence.Best(candidate.CharacterId) : null;
            row.Show(false, seen?.Reason ?? AttendanceReason.NoActivityLogged, seen?.Amount, false, null);
            row.Presence = _PresenceOf(candidate);
            rows.Add(row);
        }

        _Place(rows);
        CountText = "— in site";
    }

    private void _Count(int inSite) => CountText = $"{inSite} in site";

    private void _Describe(Role role, string? commanderName)
    {
        string commander = commanderName ?? "the fleet commander";
        NoticeText = role switch
        {
            Role.Unknown => "Who commands this fleet is not known right now, so this list cannot be set here.",
            Role.Member when _stored is null => $"Waiting for {commander}'s list.",
            Role.Member => $"Set by {commander} — ask them to change it.",
            _ => null
        };
    }

    private AttendanceRowViewModel _RowFor(AttendanceCandidate candidate)
    {
        AttendanceRowViewModel? row = Rows.FirstOrDefault(existing => existing.CharacterId == candidate.CharacterId);
        if (row is null || row.IsLocal != candidate.IsLocal || row.IsExternal != candidate.IsExternal)
            return new AttendanceRowViewModel(candidate.CharacterId, candidate.Name, candidate.IsLocal, candidate.IsExternal,
                _OnTicked, _OnEnterPayout);

        row.Name = candidate.Name;
        return row;
    }

    // Kept as they are when the order holds, so a tick is never rebound under the cursor.
    private void _Place(IReadOnlyList<AttendanceRowViewModel> rows)
    {
        if (Rows.SequenceEqual(rows))
            return;

        Rows.Clear();
        foreach (AttendanceRowViewModel row in rows)
            Rows.Add(row);
    }

    private void _OnTicked(AttendanceRowViewModel row)
    {
        if (!CanDecide)
            return;

        IReadOnlyList<AttendanceCandidate> candidates = _Candidates(_RoleNow());
        AttendanceProposalLine? line = AttendanceProposal.Propose(candidates, id => _evidence.Best(id), _lastSite, _standing)
            .FirstOrDefault(proposed => proposed.CharacterId == row.CharacterId);
        // Ticking a line back to what was proposed hands it back to the proposal.
        if (line is not null && line.IsInSite == row.IsInSite && line.Reason != AttendanceReason.SetByHand)
            _overrides.Remove(row.CharacterId);
        else
            _overrides[row.CharacterId] = row.IsInSite;

        _NoteChangeByHand();
    }

    /// <summary>A figure typed over the table's on a Local row (ET-271) — written onto that character's own run, and
    /// counted from this moment on, ahead of the store catching up.</summary>
    private void _OnEnterPayout(AttendanceRowViewModel row, decimal? amount)
    {
        if (!CanDecide || Context.Participants.FirstOrDefault(participant => participant.CharacterId == row.CharacterId)
                is not { } participant)
            return;

        _corrections[row.CharacterId] = amount;
        _Rebuild(_nowUtc);
        Context.Refresh(_nowUtc);
        _ = _WritePayoutAsync(participant.RunId, amount);
    }

    private async Task _WritePayoutAsync(Guid runId, decimal? amount)
    {
        using IServiceScope scope = Context.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Send(new SetHomefrontPayoutCommand(runId, amount));
    }

    private FleetMemberPresenceState? _PresenceOf(AttendanceCandidate candidate)
    {
        if (candidate.IsExternal || Context.FleetId is null)
            return null;

        (PresenceState State, DateTimeOffset HeardAt)? heard;
        lock (_gate)
            heard = _heard.TryGetValue(candidate.CharacterId, out (PresenceState, DateTimeOffset) value) ? value : null;
        DateTimeOffset? lastHeard = heard?.HeardAt
                                    ?? _roster?.FirstOrDefault(member => member.CharacterId == candidate.CharacterId)?.LastSeenAt;
        return AttendanceRoster.PresenceOf(Context.Services, candidate.CharacterId, candidate.IsLocal, heard?.State,
            lastHeard, new DateTimeOffset(_nowUtc, TimeSpan.Zero));
    }

    private string _NameOf(long characterId)
    {
        if (_names.TryGetValue(characterId, out string? name))
            return name;
        if (Context.FleetMembers.FirstOrDefault(member => member.CharacterId == characterId) is { } member
            && !member.Name.StartsWith("Char ", StringComparison.Ordinal))
            return _names[characterId] = member.Name;

        if (_namesAsked.Add(characterId))
            _ = _ResolveNameAsync(characterId);
        return $"Char {characterId}";
    }

    private async Task _ResolveNameAsync(long characterId)
    {
        if (await Task.Run(() => AttendanceRoster.NameOfAsync(Context.Services, characterId)) is { } name)
            _names[characterId] = name;
    }

    // ── Reading ─────────────────────────────────────────────────────────────────────────────────────

    private async Task _LoadOwnAsync()
    {
        if (_isOwnLoaded || _isLoadingOwn)
            return;

        _isLoadingOwn = true;
        try
        {
            // Every store and server read in this section runs off the UI thread (ET-287): Microsoft.Data.Sqlite's
            // async API does its work synchronously, so awaiting it straight from a clock tick blocks the window.
            _own = await Task.Run(() => AttendanceRoster.OwnCharacterIdsAsync(Context.Services));
            _isOwnLoaded = true;
        }
        finally
        {
            _isLoadingOwn = false;
        }
    }

    private async Task _ReadRosterIfDueAsync(DateTime nowUtc)
    {
        if (Context.FleetId is not { } fleetId)
        {
            _roster = null;
            return;
        }

        if (_isReadingRoster || (_rosterFleetId == fleetId && _rosterReadAtUtc is { } readAt
                                                           && nowUtc - readAt < RosterReadInterval && nowUtc >= readAt))
            return;

        _isReadingRoster = true;
        try
        {
            _rosterReadAtUtc = nowUtc;
            IReadOnlyList<RosterCharacter>? roster = await Task.Run(() => AttendanceRoster.ReadAsync(Context.Services, fleetId));
            // An unreadable roster keeps the last one read: a server that blinks must not empty the list.
            if (roster is not null)
            {
                _roster = roster;
                _rosterFleetId = fleetId;
            }
        }
        finally
        {
            _isReadingRoster = false;
        }
    }

    private async Task _ReadStoredIfDueAsync(DateTime nowUtc)
    {
        if (_isReadingStored || Context.RunId is not { } runId
            || (!_isStoredStale && _storedReadAtUtc is { } readAt && nowUtc - readAt < StoredReadInterval && nowUtc >= readAt))
            return;
        if (Context.Services.GetService<CqrsDispatcher>() is null)
            return;

        _isReadingStored = true;
        try
        {
            _isStoredStale = false;
            string? key = _KeyOf(Context);
            string? groupCode = Context.GroupCode;
            Result<RunAttendanceDecision?> read = await Task.Run(async () =>
            {
                using IServiceScope scope = Context.Services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
                    .Query(new GetRunAttendanceQuery(groupCode, runId));
            });
            // The window moved on to another run while this was read: this list is the last run's, not this one's.
            if (!read.IsSuccess || key != _KeyOf(Context))
                return;

            _stored = read.Value;
            _storedReadAtUtc = nowUtc;
            if (_stored is { } stored && stored.SetAtUtc > _lastSetAtUtc)
                _lastSetAtUtc = stored.SetAtUtc;
            // Decided somewhere else since this window last spoke, and nothing clicked here waits to be written: take
            // that list up whole — outcome, ticks and all — instead of writing this window's older view back over it.
            if (_stored is { } newer && _isStandingTaken && newer.SetAtUtc > _knownSetAtUtc && !_isChangedByHand)
            {
                _isStandingTaken = false;
                _overrides.Clear();
                _isOutcomeSetByHand = false;
            }
        }
        finally
        {
            _isReadingStored = false;
        }
    }

    private async Task _ReadLastSiteIfDueAsync()
    {
        if (Context.FleetId is not { } fleetId || Context.GroupCode is not { } groupCode || _lastSiteGroupCode == groupCode
            || Context.Services.GetService<CqrsDispatcher>() is null)
            return;

        _lastSiteGroupCode = groupCode;
        Result<RunAttendanceDecision?> read = await Task.Run(async () =>
        {
            using IServiceScope scope = Context.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
                .Query(new GetFleetAttendanceBaseQuery(fleetId, groupCode));
        });
        _lastSite = read.IsSuccess ? read.Value : null;
    }

    // ── Writing ─────────────────────────────────────────────────────────────────────────────────────

    /// <param name="isForced">SAVE is waiting on it: written now, whatever the bundle window or a write already under
    /// way (waited out rather than skipped).</param>
    private async Task _WriteIfDueAsync(DateTime nowUtc, bool isForced = false)
    {
        if (isForced)
            await _writeGate.WaitAsync();
        else if (!_writeGate.Wait(0))
            return;

        try
        {
            await _WriteUnderGateAsync(nowUtc, isForced);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task _WriteUnderGateAsync(DateTime nowUtc, bool isForced)
    {
        Role role = _RoleNow();
        if (role is not (Role.Pilot or Role.Commander) || Context.RunId is not { } runId
            || Context.RunState is not (ActivityRunState.Running or ActivityRunState.Stopped or ActivityRunState.Saved)
            || _storedReadAtUtc is null || !_isOwnLoaded)
            return;
        // Once the run is committed only a click in this window is written — a pick made just before or after SAVE is
        // kept, and nothing else rewrites a saved list by itself.
        if (_isClosed && !_isChangedByHand && !isForced)
            return;
        // A commander already decided these runs; a pilot's own list would never take (SetRunAttendanceCommand).
        if (role is Role.Pilot && _stored?.Source is AttendanceSource.FleetCommander)
            return;

        RunAttendanceDecision current = _CurrentDecision(_Candidates(role), role, nowUtc);
        // What the window works out by itself is written again only when it decides something new (ET-287): the
        // evidence figure beside each tick grows with every mining cycle and every volley, and comparing it too wrote
        // the whole list onto every run of the group every bundle window. A click and SAVE still write the figures.
        bool isWrittenWhole = isForced || _isChangedByHand;
        bool isNew = _stored is not { } stored
                     || !(isWrittenWhole ? stored.ListsTheSameAs(current) : stored.DecidesTheSameAs(current))
                     || stored.Source != current.Source
                     || stored.SetByCharacterId != current.SetByCharacterId;
        if (!isNew)
        {
            _changedSinceUtc = null;
            _isChangedByHand = false;
            if (role is Role.Commander && !_isClosed && _stored is { } standing
                && (_sentAtUtc is not { } sentAt || nowUtc - sentAt >= ResendInterval || nowUtc < sentAt))
                await _SendAsync(standing, nowUtc);
            return;
        }

        // The first list of a run — its default outcome with it — is stored at once; only later changes the window
        // makes by itself (evidence, a roster read) wait out the bundle window.
        _changedSinceUtc ??= nowUtc;
        if (!isForced && _stored is not null && nowUtc - _changedSinceUtc < BundleWindow && nowUtc >= _changedSinceUtc)
            return;

        // Never stamped earlier than the last one: a receiver keeps the newest, and a clock set back must not turn a
        // correction into old news it ignores. Whole milliseconds, the wire's own grain, so the commander's copy and
        // every member's are the same instant.
        _lastSetAtUtc = RunGroupAttendance.ToWireInstant(nowUtc > _lastSetAtUtc ? nowUtc : _lastSetAtUtc.AddMilliseconds(1));
        RunAttendanceDecision decision = current with { SetAtUtc = _lastSetAtUtc };
        // A click goes over whatever stands; anything this window worked out by itself only over the list it was
        // worked out from.
        SetRunAttendanceCommand command = new(decision, [.. _own], Context.GroupCode,
            Context.GroupCode is null ? runId : null,
            IsProposal: !_isChangedByHand, StandingSetAtUtc: _stored?.SetAtUtc);
        int handChangeCount = _handChangeCount;
        Result<int> written = await Task.Run(async () =>
        {
            using IServiceScope scope = Context.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Send(command);
        });
        if (!written.IsSuccess)
            return;
        // Nothing took it — the store already holds this or a list that outranks it: read what it holds.
        if (written.Value == 0)
        {
            _isStoredStale = true;
            return;
        }

        _stored = decision;
        _knownSetAtUtc = decision.SetAtUtc;
        // A click made while this was written is not in it: it stays owed, and its own forced write follows.
        if (handChangeCount == _handChangeCount)
        {
            _changedSinceUtc = null;
            _isChangedByHand = false;
        }
        if (role is Role.Commander)
            await _SendAsync(decision, nowUtc);
    }

    private async Task _SendAsync(RunAttendanceDecision decision, DateTime nowUtc)
    {
        _sentAtUtc = nowUtc;
        if (Context.FleetId is not { } fleetId || Context.GroupCode is not { } groupCode
            || _CommanderId() is not { } commander || Context.Services.GetService<IEventBus>() is not { } eventBus)
            return;

        // A client-only fleet lives on this machine alone: its runs are all this client's, already written above.
        if (Context.Services.GetService<IFleetParticipation>()?.Current
                .FirstOrDefault(participant => participant.FleetId == fleetId) is { ClientOnly: true })
            return;

        await eventBus.PublishAsync(new FleetRunAttendanceEvent(new RunGroupAttendance(
                fleetId, groupCode, RunGroupAttendance.ToUnixMs(decision.SetAtUtc), decision.Entries,
                decision.NotOnRosterCount, decision.Outcome, decision.CompletedWaveCount, decision.OutcomeFromGameLog), commander),
            EventTarget.Remote);
    }

    // ── The pale shadow line (ET-262) ──────────────────────────────────────────────────────────────────

    /// <summary>The gamelog watcher's pump thread (or, on RESUME, ET-258's catch-up read) saw a Metaliminal
    /// Meteoroid's asteroid run dry. Only latched here — applied on the next tick, on this window's own thread, the
    /// same split <see cref="_OnFleetMetric"/> already uses for <see cref="_heard"/>.</summary>
    private void _OnHomefrontCompletion(int characterId, DateTime atUtc)
    {
        lock (_gate)
            _paleShadow = (characterId, atUtc);
    }

    /// <summary>Marks the outcome as completed from the game log the first time this run's own gamelog shows the pale
    /// shadow line — over the default, never over a pick somebody made (including "failed" or "unknown"), and never
    /// for a kind other than Metaliminal Meteoroid, whose asteroid is the only site this line means anything for.</summary>
    private void _ApplyGameLogOutcomeIfDue(DateTime nowUtc)
    {
        if (!CanDecide || Outcome is not (null or HomefrontOutcome.Completed) || OutcomeIsFromGameLog || _isOutcomeSetByHand
            || Context.RunType.HomefrontKind != "Metaliminal Meteoroid")
            return;

        (int CharacterId, DateTime AtUtc)? paleShadow;
        lock (_gate)
            paleShadow = _paleShadow;
        if (paleShadow is not { } observed || !_own.Contains(observed.CharacterId) || Context.EffectiveStartUtc is not { } start
            || observed.AtUtc < start || (Context.EffectiveStopUtc is { } stop && observed.AtUtc > stop))
            return;

        Outcome = HomefrontOutcome.Completed;
        OutcomeIsFromGameLog = true;
        _changedSinceUtc ??= nowUtc;
    }

    // ── Evidence ────────────────────────────────────────────────────────────────────────────────────

    private void _OnContribution(int characterId, SiteContribution kind, int amount, DateTime atUtc) =>
        _evidence.Note(characterId, kind, amount, atUtc);

    /// <summary>What another pilot's client says of their own character: damage or mining on the live stream, and
    /// their presence. This client's own characters are read from its own gamelog instead.</summary>
    private void _OnFleetMetric(FleetMetricEvent integrationEvent)
    {
        MetricSample sample = integrationEvent.Data;
        if (Context.FleetId is not { } fleetId || sample.FleetId != fleetId || _own.Contains(sample.CharacterId))
            return;

        DateTimeOffset at = DateTimeOffset.FromUnixTimeMilliseconds(sample.UnixMs);
        lock (_gate)
        {
            PresenceState state = sample.Kind is MetricKind.Presence
                ? (PresenceState)(int)sample.Value
                : _heard.GetValueOrDefault(sample.CharacterId).State;
            _heard[sample.CharacterId] = (state, at);
        }

        if (sample.Kind is MetricKind.Dps or MetricKind.MiningYield && sample.Value > 0)
            _evidence.NoteFleetActivity(sample.CharacterId, at.UtcDateTime);
    }

    private void _NoteSharedMining()
    {
        if (Context.GroupCode is not { } groupCode || Context.Services.GetService<FleetRunShares>() is not { } shares)
            return;

        foreach ((int characterId, RunShareUpdate share) in shares.Of(groupCode))
            if (share.SharesMining && share.MinedUnits > 0 && !_own.Contains(characterId))
                _evidence.NoteFleetActivity(characterId, DateTimeOffset.FromUnixTimeMilliseconds(share.UnixMs).UtcDateTime);
    }

    private void _OnAttendanceApplied(string groupCode)
    {
        if (groupCode == Context.GroupCode)
            _isStoredStale = true;
    }

    private void _OnRunsChanged(RunsChangedEvent changed)
    {
        if (changed.Data.GroupCode is { } groupCode ? groupCode == Context.GroupCode : changed.Data.RunId == Context.RunId)
            _isStoredStale = true;
    }
}
