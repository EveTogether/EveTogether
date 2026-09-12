using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs.Attendance;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.Extensions.DependencyInjection;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// HOMEFRONT in the run window (ET-230): who was in the site when it completed — the fact a homefront pays per
/// character on — as one flat list of every character on the fleet's roster, this client's own ones tagged "Local".
///
/// The app proposes (<see cref="AttendanceProposal"/>); one person decides. In a fleet that is whoever commands it,
/// and their list goes to every member as <c>fleet.run-attendance</c>, where each client writes it onto its own runs
/// (<see cref="FleetRunAttendance"/>) and shows it read-only. Without a fleet the pilot decides for their own runs.
/// A member's window never writes a list of its own: it shows the commander's, or says it is waiting for it.
///
/// The decision is written as it is made, after the same short bundle window the fleet share waits (ET-242), so the
/// members see it without reopening anything; the commander's window sends it again every half minute for a member
/// who connected late. Presence beside each name is the live fleet view and is never stored.
/// </summary>
public sealed partial class HomefrontWindowSectionViewModel : RunWindowSection
{
    /// <summary>Ticks come in bursts — a commander running down the list — so a change waits this long and goes out
    /// once.</summary>
    public static readonly TimeSpan BundleWindow = TimeSpan.FromSeconds(2);

    /// <summary>The commander's list goes out again this often while the window is open, for a member whose client
    /// connected after the last change — the server keeps no fleet message for anyone.</summary>
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RosterReadInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StoredReadInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly AttendanceEvidenceCollector _evidence = new();
    private readonly Dictionary<long, bool> _overrides = [];
    private readonly Dictionary<long, string> _names = [];
    private readonly HashSet<long> _namesAsked = [];
    private readonly Dictionary<long, (PresenceState State, DateTimeOffset HeardAt)> _heard = [];
    private readonly GamelogClientService? _gamelog;
    private readonly FleetRunAttendance? _attendance;
    private readonly IDisposable? _metricSubscription;

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
    private bool _isWriting;
    private bool _isClosed;
    private int? _fleetSizeAtStop;
    private DateTime _nowUtc = DateTime.UtcNow;

    public HomefrontWindowSectionViewModel(IRunWindowContext context)
        : base(context, RunSectionId.Homefront, "HOMEFRONT")
    {
        IsExpanded = true;
        _gamelog = context.Services.GetService<GamelogClientService>();
        if (_gamelog is not null)
            _gamelog.ContributionObserved += _OnContribution;
        _attendance = context.Services.GetService<FleetRunAttendance>();
        if (_attendance is not null)
            _attendance.Applied += _OnAttendanceApplied;
        _metricSubscription = context.Services.GetService<IEventBus>()?.Subscribe<FleetMetricEvent>(_OnFleetMetric);
    }

    /// <summary>One flat list: this client's own characters first, the rest A–Z. Never grouped by player — which
    /// characters belong to one player is not known here (Jithran, 2026-09-11).</summary>
    public ObservableCollection<AttendanceRowViewModel> Rows { get; } = [];

    /// <summary>This client decides the list: it commands the fleet, or flies without one.</summary>
    [ObservableProperty] private bool _canDecide;

    [ObservableProperty] private string _decidedByText = string.Empty;

    /// <summary>For a member: when the commander last changed the list, and when this client received it.</summary>
    [ObservableProperty] private string? _lastChangeText;

    /// <summary>"6 in fleet · 5 in site" — the fleet and N, side by side and never the same number.</summary>
    [ObservableProperty] private string _countText = string.Empty;

    [ObservableProperty] private string _breakdownText = string.Empty;

    /// <summary>Pilots in the site who are on no roster at all — a stranger who joined in. They count in N.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DecreaseNotOnRosterCommand))]
    private int _notOnRosterCount;

    /// <summary>What the list is waiting on or why it cannot be changed here, in words; null when there is nothing
    /// to say.</summary>
    [ObservableProperty] private string? _noticeText;

    [ObservableProperty] private string _captionText = string.Empty;

    /// <summary>Said when the list started from the previous homefront of this fleet (Jithran, 2026-09-11).</summary>
    [ObservableProperty] private string? _lastSiteText;

    public override void Refresh(DateTime nowUtc)
    {
        _nowUtc = nowUtc;
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

    public override void RefreshSummary() => HeaderSummary = CountText;

    public override void OnRunStarted()
    {
        _isClosed = false;
        _isStoredStale = true;
    }

    public override void OnRunClosed() => _isClosed = true;

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
            _gamelog.ContributionObserved -= _OnContribution;
        if (_attendance is not null)
            _attendance.Applied -= _OnAttendanceApplied;
        _metricSubscription?.Dispose();
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
        _changedSinceUtc ??= _nowUtc;
        _Rebuild(_nowUtc);
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

    private Role _RoleNow()
    {
        if (Context.GroupCode is null || Context.FleetId is null)
            return Role.Pilot;

        return _CommanderId() switch
        {
            null => Role.Unknown,
            { } commander when _own.Contains(commander) => Role.Commander,
            _ => Role.Member
        };
    }

    // ── The list ────────────────────────────────────────────────────────────────────────────────────

    private void _Rebuild(DateTime nowUtc)
    {
        Role role = _RoleNow();
        CanDecide = role is Role.Pilot or Role.Commander;
        if (CanDecide && !_isStandingTaken && _storedReadAtUtc is not null)
        {
            // Picked up once, the first time this window decides with the store read: a reopened window or a new
            // commander goes on from the list as it stands instead of starting it over.
            _standing = _stored;
            _isStandingTaken = true;
            NotOnRosterCount = _stored?.NotOnRosterCount ?? NotOnRosterCount;
        }
        else if (!CanDecide)
            _isStandingTaken = false;

        IReadOnlyList<AttendanceCandidate> candidates = _Candidates(role);
        string? commanderName = _CommanderId() is { } commander ? _NameOf(commander) : null;

        if (CanDecide)
            _ShowDecision(candidates, _CurrentDecision(candidates, role, nowUtc), commanderName: null);
        else if (_stored is { } stored)
            _ShowDecision(candidates, stored, commanderName);
        else
            _ShowWaiting(candidates);

        _Describe(role, commanderName);
        RefreshSummary();
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

        if (Context.FleetId is not null)
            foreach (RosterCharacter member in _roster ?? [])
                candidates.TryAdd(member.CharacterId, new AttendanceCandidate(member.CharacterId, _NameOf(member.CharacterId),
                    _own.Contains(member.CharacterId), member.IsExternal));

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

        long setBy = role is Role.Commander && _CommanderId() is { } commander
            ? commander
            : Context.ActingCharacterId ?? Context.RunCharacterId ?? 0;
        return new RunAttendanceDecision(entries, NotOnRosterCount,
            role is Role.Commander ? AttendanceSource.FleetCommander : AttendanceSource.Pilot, setBy, nowUtc);
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
                isByHand ? (CanDecide ? "you" : commanderName ?? "the fleet commander") : null);
            row.Presence = _PresenceOf(candidate);
            rows.Add(row);
        }

        _Place(rows);
        _Count(decision.Entries.Count(entry => entry.IsInSite), decision.NotOnRosterCount);
        if (!CanDecide)
            NotOnRosterCount = decision.NotOnRosterCount;
    }

    /// <summary>
    /// The reason beside a tick. A line set by hand still shows what the evidence said — "set by" beside it says the
    /// rest, and the one who decides sees why the proposal disagreed. A member's own characters show their own
    /// gamelog's evidence beside the commander's tick, which this client can see better than the commander can.
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
        CountText = _roster is { } roster && Context.FleetId is not null ? $"{roster.Count} in fleet · — in site" : "— in site";
        BreakdownText = string.Empty;
    }

    private void _Count(int ticked, int notOnRoster)
    {
        int inSite = ticked + notOnRoster;
        CountText = _roster is { } roster && Context.FleetId is not null
            ? $"{roster.Count} in fleet · {inSite} in site"
            : $"{inSite} in site";
        BreakdownText = $"{ticked} ticked + {notOnRoster} not on the roster";
    }

    private void _Describe(Role role, string? commanderName)
    {
        string commander = commanderName ?? "the fleet commander";
        DecidedByText = role switch
        {
            Role.Commander => "you · fleet commander",
            Role.Pilot => "you",
            Role.Member => $"🔒 {commander} · fleet commander",
            _ => "not known right now"
        };

        CaptionText = role switch
        {
            Role.Commander => "Proposed from this run's evidence — you decide. Every member sees this list as you set it, read-only.",
            Role.Pilot => "Proposed from this run's gamelog — you decide who was in the site when it completed.",
            _ => $"Only {commander} changes this list. If it is wrong, tell them — this client applies what they set to its " +
                 "local characters, and never overrides it with a guess of its own."
        };

        NoticeText = role switch
        {
            Role.Unknown => "Who commands this fleet is not known right now, so this list cannot be set here.",
            Role.Member when _stored is null => $"Waiting for {commander}'s list.",
            _ => null
        };

        LastChangeText = role is Role.Member or Role.Unknown && _stored is { } stored
            ? $"last change {stored.SetAtUtc.ToLocalTime():HH:mm}" + (Context.GroupCode is { } groupCode
                && _attendance?.ReceivedAtUtc(groupCode) is { } received
                    ? $" · received {received.ToLocalTime():HH:mm}"
                    : string.Empty)
            : null;

        LastSiteText = CanDecide && _lastSite is not null
            ? "Started from the list the last homefront in this fleet ended with."
            : null;
    }

    private AttendanceRowViewModel _RowFor(AttendanceCandidate candidate)
    {
        AttendanceRowViewModel? row = Rows.FirstOrDefault(existing => existing.CharacterId == candidate.CharacterId);
        if (row is null || row.IsLocal != candidate.IsLocal || row.IsExternal != candidate.IsExternal)
            return new AttendanceRowViewModel(candidate.CharacterId, candidate.Name, candidate.IsLocal, candidate.IsExternal,
                _OnTicked);

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

        _changedSinceUtc ??= _nowUtc;
        _Rebuild(_nowUtc);
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
        if (await AttendanceRoster.NameOfAsync(Context.Services, characterId) is { } name)
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
            _own = await AttendanceRoster.OwnCharacterIdsAsync(Context.Services);
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
            IReadOnlyList<RosterCharacter>? roster = await AttendanceRoster.ReadAsync(Context.Services, fleetId);
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
            using IServiceScope scope = Context.Services.CreateScope();
            Result<RunAttendanceDecision?> read = await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
                .Query(new GetRunAttendanceQuery(Context.GroupCode, runId));
            if (!read.IsSuccess)
                return;

            _stored = read.Value;
            _storedReadAtUtc = nowUtc;
            if (_stored is { } stored && stored.SetAtUtc > _lastSetAtUtc)
                _lastSetAtUtc = stored.SetAtUtc;
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
        using IServiceScope scope = Context.Services.CreateScope();
        Result<RunAttendanceDecision?> read = await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>()
            .Query(new GetFleetAttendanceBaseQuery(fleetId, groupCode));
        _lastSite = read.IsSuccess ? read.Value : null;
    }

    // ── Writing ─────────────────────────────────────────────────────────────────────────────────────

    private async Task _WriteIfDueAsync(DateTime nowUtc)
    {
        Role role = _RoleNow();
        if (_isWriting || _isClosed || role is not (Role.Pilot or Role.Commander) || Context.RunId is not { } runId
            || Context.RunState is not (ActivityRunState.Running or ActivityRunState.Stopped)
            || _storedReadAtUtc is null || !_isOwnLoaded)
            return;
        // A commander already decided these runs; a pilot's own list would never take (SetRunAttendanceCommand).
        if (role is Role.Pilot && _stored?.Source is AttendanceSource.FleetCommander)
            return;

        RunAttendanceDecision current = _CurrentDecision(_Candidates(role), role, nowUtc);
        bool isNew = _stored is not { } stored || !stored.ListsTheSameAs(current) || stored.Source != current.Source
                     || stored.SetByCharacterId != current.SetByCharacterId;
        if (!isNew)
        {
            _changedSinceUtc = null;
            if (role is Role.Commander && _stored is { } standing
                && (_sentAtUtc is not { } sentAt || nowUtc - sentAt >= ResendInterval || nowUtc < sentAt))
                await _SendAsync(standing, nowUtc);
            return;
        }

        _changedSinceUtc ??= nowUtc;
        if (nowUtc - _changedSinceUtc < BundleWindow && nowUtc >= _changedSinceUtc)
            return;

        _isWriting = true;
        try
        {
            // Never stamped earlier than the last one: a receiver keeps the newest, and a clock set back must not turn
            // a correction into old news it ignores. Whole milliseconds, the wire's own grain, so the commander's copy
            // and every member's are the same instant.
            _lastSetAtUtc = RunGroupAttendance.ToWireInstant(nowUtc > _lastSetAtUtc ? nowUtc : _lastSetAtUtc.AddMilliseconds(1));
            RunAttendanceDecision decision = current with { SetAtUtc = _lastSetAtUtc };
            using (IServiceScope scope = Context.Services.CreateScope())
            {
                Result<int> written = await scope.ServiceProvider.GetRequiredService<CqrsDispatcher>().Send(
                    new SetRunAttendanceCommand(decision, [.. _own], Context.GroupCode,
                        Context.GroupCode is null ? runId : null));
                if (!written.IsSuccess)
                    return;
                // Nothing took it — the store already holds this or a list that outranks it: read what it holds.
                if (written.Value == 0)
                {
                    _isStoredStale = true;
                    return;
                }
            }

            _stored = decision;
            _changedSinceUtc = null;
            if (role is Role.Commander)
                await _SendAsync(decision, nowUtc);
        }
        finally
        {
            _isWriting = false;
        }
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
                decision.NotOnRosterCount), commander),
            EventTarget.Remote);
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
}
