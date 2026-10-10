using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Gamelog;
using EveUtils.Client.ViewModels.Activity;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// ENEMIES in the run window: one row per enemy type, on its own rather than inside ACTIVITY — a site's worth of rats
/// pushed every other section under the fold (ET-115). The count is typed after the fight, so the rows outlive STOP.
///
/// One collector per character in the group, each fed only by that character's own gamelog (ET-210 review finding,
/// 2026-09-09, round 4: Jithran chose per-character tracking with a group total over one shared tally). Keyed on
/// characterId rather than on which run the window is currently showing, so switching the column never touches a
/// character's own count: nothing here is ever reassigned or cleared for one character because another one was
/// clicked.
///
/// The same per-character watch keeps each character's combat lines for the run's timeline (ET-467), so both ride
/// one lifecycle: started with the run, fed live and by catch-up, handed to SAVE, let go at close.
///
/// A pilot out of a fleet run while a mate is still inside sees that mate's rooms and enemies here instead (ET-498),
/// read off the mate's <see cref="RunShareUpdate"/>. Shown only: SAVE and LOOT keep reading the pilot's own collectors.
/// </summary>
public sealed partial class EnemiesWindowSectionViewModel : RunWindowSection
{
    private readonly GamelogClientService? _gamelog;
    private readonly Dictionary<int, RunEnemyObservationCollector> _collectors = [];
    private readonly Dictionary<int, List<GameLogEvent>> _combatEvents = [];
    private readonly IEventBus? _bus;
    private readonly IDisposable? _roomsSubscription;
    private readonly IDisposable? _proposalSubscription;
    private bool _wasCommander;
    private FleetMateRooms? _mate;

    public EnemiesWindowSectionViewModel(IRunWindowContext context) : base(context, RunSectionId.Enemies, "ENEMIES")
    {
        _gamelog = context.Services.GetService<GamelogClientService>();
        if (_gamelog is not null)
        {
            _gamelog.CombatObserved += _OnCombatObserved;
            _gamelog.TelemetryObserved += _OnTelemetryObserved;
        }

        // In a fleet abyssal every log helps find the rooms and the commander's list is the fleet's (ET-494).
        _bus = context.Services.GetService<IEventBus>();
        _roomsSubscription = _bus?.Subscribe<FleetRunGroupRoomsEvent>(_OnCommanderRooms);
        _proposalSubscription = _bus?.Subscribe<FleetRunGroupRoomProposedEvent>(_OnRoomProposed);
    }

    /// <summary>The on-screen character's own sightings — whichever run the column is currently showing. Every other
    /// group member's own collector keeps counting in the background regardless (ET-210 round 4).</summary>
    public IReadOnlyList<RunEnemyObservationViewModel> EnemyObservations =>
        _mate?.Rows
        ?? (Context.RunCharacterId is { } id && _collectors.TryGetValue(id, out RunEnemyObservationCollector? collector)
            ? collector.Observations
            : []);

    /// <summary>The fleet mate whose rooms and enemies this section shows (ET-498), or null for the pilot's own.</summary>
    public string? FleetMateName => _mate?.Name;

    /// <summary>The on-screen character's rows per room, newest room first (ET-240) — empty while the run has no
    /// rooms, and then the flat list above is the whole of it, exactly as before NEW ROOM existed.</summary>
    public IReadOnlyList<RunEnemyRoomViewModel> EnemyRooms { get; private set; } = [];

    public bool HasRooms => EnemyRooms.Count > 0;

    /// <summary>The rows or rooms of the on-screen pilot changed, or a name without a type was seen (ET-369).</summary>
    public event Action? SightingsChanged;

    /// <summary>Everything the on-screen pilot saw, per room, for TARGETS: the rows, and the names the SDE has no
    /// type for. Read only — TARGETS writes nothing here.</summary>
    public IReadOnlyList<TargetSighting> TargetSightings() => _OnScreenCollector() is not { } collector
        ? []
        :
        [
            .. collector.Observations.Select(SightingOf),
            .. collector.UnresolvedSightings.Select(seen => new TargetSighting(
                RunRooms.RoomOf(collector.RoomBoundaries, seen.FirstObservedAtUtc), seen.Name, null))
        ];

    /// <summary>A row as TARGETS reads it: a Tyrannos agent's negative id is no SDE type, so it goes by name.</summary>
    internal static TargetSighting SightingOf(RunEnemyObservationViewModel observation) => new(observation.RoomNumber,
        observation.EnemyName, observation.EnemyTypeId > 0 ? observation.EnemyTypeId : null);

    /// <summary>The on-screen pilot's combat, neut and rep lines of the run, each with its room, for TARGETS. Read only.</summary>
    public IReadOnlyList<(int? Room, GameLogEvent Event)> TargetEvents() =>
        _OnScreenCollector() is not { } collector || !_combatEvents.TryGetValue(Context.RunCharacterId!.Value, out List<GameLogEvent>? events)
            ? []
            : [.. events.Select(logEvent => (RunRooms.RoomOf(collector.RoomBoundaries, logEvent.Timestamp), logEvent))];

    /// <summary>The window's line under the clock while the run has rooms: which room, since when, for how long. A
    /// mate's room is still going, so its time runs on the clock rather than stopping with this pilot's own leg.</summary>
    public string? CurrentRoomText(DateTime nowUtc)
    {
        IReadOnlyList<DateTime> boundaries = _Boundaries();
        if (boundaries.Count == 0)
        {
            return null;
        }

        DateTime since = boundaries[^1];
        DateTime until = _mate is null ? Context.EffectiveStopUtc ?? nowUtc : nowUtc;
        TimeSpan inRoom = TimeSpan.FromTicks(Math.Max(0, (until - since).Ticks));
        string? faction = _FactionText(boundaries.Count + 1);
        return $"ROOM {boundaries.Count + 1}  since {since.ToLocalTime():HH:mm:ss} · {(int)inRoom.TotalMinutes:00}:{inRoom.Seconds:00}"
               + (faction is null ? string.Empty : $" · {faction}")
               + (_mate is null ? string.Empty : $" · {_mate.Name}");
    }

    /// <summary>Names the SDE has no type for, shown as plain rows while the run has no rooms (ET-369).</summary>
    public IReadOnlyList<string> UnresolvedNames => _UnresolvedIn(null);

    /// <summary>The AUTO badge for the room going on now, when the detector opened it (ET-368), or FC when it is the
    /// commander's (ET-494). None while a fleet mate's rooms are shown (ET-498).</summary>
    public RoomSourceViewModel? CurrentRoomSource =>
        _mate is null && _OnScreenCollector() is { DetectedCertainties: { Count: > 0 } certainties } collector
            ? RoomSourceViewModel.Of(certainties[^1], collector.IsFollowingCommander)
            : null;

    /// <summary>The on-screen pilot's room boundaries, oldest first.</summary>
    internal IReadOnlyList<DateTime> RoomBoundaries => _OnScreenCollector()?.RoomBoundaries ?? [];

    /// <summary>Whether the on-screen pilot's rooms are still found by the detector (ET-368).</summary>
    public bool IsDetecting => _OnScreenCollector()?.IsDetecting == true;

    /// <summary>NEW ROOM (ET-240): close the current room now and begin the next. The STOP rule decides whose: in a run
    /// whose clock is per pilot only the pilot on screen, otherwise every own toon in the group.</summary>
    public void StartRoom(DateTime nowUtc)
    {
        if (Context.RunState != ActivityRunState.Running)
        {
            return;
        }

        foreach (RunEnemyObservationCollector collector in _RoomScope())
        {
            collector.StartRoom(nowUtc);
        }

        _ShowRooms();
    }

    /// <summary>Undo on the last room: the same pilots NEW ROOM reached take its boundary back.</summary>
    [RelayCommand]
    private void UndoRoom()
    {
        if (!Context.CanControl)
        {
            return;
        }

        foreach (RunEnemyObservationCollector collector in _RoomScope())
        {
            collector.UndoRoom();
        }

        _ShowRooms();
    }

    /// <summary>Shut, the section still has to answer both halves of the question it exists for: which kinds were
    /// seen, and how many of them carry a count. Zero means "seen, not counted", so "seen" and "counted"
    /// are different numbers and the header is the only place they are both visible.</summary>
    public override void RefreshSummary()
    {
        int types = EnemyObservations.Count;
        if (types == 0)
        {
            HeaderSummary = Context.RunState == ActivityRunState.NotStarted ? "no run watched yet" : "no enemies seen yet";
            return;
        }

        int counted = EnemyObservations.Count(observation => observation.IsCounted);
        int boundaries = _Boundaries().Count;
        string rooms = boundaries > 0 ? $"{boundaries + 1} rooms · " : string.Empty;
        string mate = _mate is null ? string.Empty : $"{_mate.Name} · ";
        HeaderSummary = $"{mate}{rooms}{types} {(types == 1 ? "type" : "types")} · {(counted == 0 ? "none counted" : $"{counted} counted")}";
    }

    /// <summary>What <paramref name="characterId"/>'s own tally says of the run's rooms and enemies, for the fleet
    /// (ET-498). Never a mate's: what this section shows of one is not this pilot's to pass on.</summary>
    public (IReadOnlyList<long> RoomStartsUnixMs, IReadOnlyList<RunShareEnemyLine> Enemies) ShareOf(int characterId)
    {
        if (!_collectors.TryGetValue(characterId, out RunEnemyObservationCollector? collector))
        {
            return ([], []);
        }

        return (
            [.. collector.RoomBoundaries.Select(boundary => new DateTimeOffset(DateTime.SpecifyKind(boundary, DateTimeKind.Utc)).ToUnixTimeMilliseconds())],
            [
                .. collector.Observations.Select(observation =>
                    new RunShareEnemyLine(observation.EnemyName, observation.EnemyTypeId, observation.RoomNumber, observation.Count)),
                .. collector.UnresolvedSightings.Select(seen =>
                    new RunShareEnemyLine(seen.Name, null, RunRooms.RoomOf(collector.RoomBoundaries, seen.FirstObservedAtUtc), 0))
            ]);
    }

    /// <summary>Show this mate's rooms and enemies in place of the pilot's own (ET-498), or the pilot's own again for
    /// null. The rows are rebuilt only when the mate sent something new, so the list does not flicker every tick.</summary>
    public void ShowFleetMate(string? name, RunShareUpdate? share)
    {
        if (name is null || share is null)
        {
            if (_mate is null)
            {
                return;
            }

            _mate = null;
        }
        else
        {
            if (_mate is { } shown && shown.Name == name && shown.UnixMs == share.UnixMs)
            {
                return;
            }

            _mate = FleetMateRooms.From(name, share);
        }

        OnPropertyChanged(nameof(EnemyObservations));
        OnPropertyChanged(nameof(FleetMateName));
        _ShowRooms();
    }

    /// <summary>Give the on-screen character its own tally, if it does not have one yet.</summary>
    public override void OnRunStarted()
    {
        if (Context.RunCharacterId is { } id)
            _Ensure(id);

        OnPropertyChanged(nameof(EnemyObservations));
        _ShowRooms();
    }

    /// <summary>The undo button follows who may steer the run.</summary>
    protected override void OnContextChanged(string? propertyName)
    {
        if (propertyName is nameof(IRunWindowContext.CanControl))
        {
            // A pilot who just took command tells the fleet the rooms they hold, so the list never waits on a change.
            if (Context.IsFleetCommander && !_wasCommander)
            {
                _AnnounceRooms();
            }

            _wasCommander = Context.IsFleetCommander;
            _ShowRooms();
        }
    }

    /// <summary>Keeps LOOT's blocks on the same boundaries — a block appears or the clock stops after a room was set.</summary>
    public override void Refresh(DateTime nowUtc) => _ShowLootRooms();

    /// <summary>Called for a sibling the moment its own <c>StartRunCommand</c> is sent, so its gamelog is watched for
    /// enemies from the same instant its bounty and loot start counting (ET-210 review finding, round 4).</summary>
    public override void OnCharacterRunStarted(int characterId) => _Ensure(characterId);

    /// <summary>One sighting from the gamelog catch-up read (ET-258), applied straight to this character's own
    /// collector — the same effect <see cref="_OnCombatObserved"/> has live, without going through
    /// <see cref="GamelogClientService.CombatObserved"/>, which only fires from <c>AddHitAsync</c>: the DPS-feeding
    /// path a bounded historical read must never call.</summary>
    internal void RecordCatchUpSighting(int characterId, string target, DateTime observedAtUtc) =>
        _collectors.GetValueOrDefault(characterId)?.Record(characterId, target, observedAtUtc);

    /// <summary>One line from the same catch-up read, for the run's timeline: what <see cref="_OnTelemetryObserved"/>
    /// does live. Lines that are not combat, repair, neut, capacitor or e-war are ignored here.</summary>
    internal void RecordCatchUpTelemetry(int characterId, GameLogEvent logEvent)
    {
        if (logEvent is CombatEvent or RemoteRepEvent or NeutEvent or CapTransferEvent or EwarEvent)
        {
            _combatEvents.GetValueOrDefault(characterId)?.Add(logEvent);
        }
    }

    /// <summary>Let go of every character's list — the whole group's, since STOP, SAVE and DISCARD act on the whole
    /// group (ET-210).</summary>
    public override void OnRunClosed()
    {
        foreach (RunEnemyObservationCollector collector in _collectors.Values)
        {
            collector.Changed -= RefreshSummary;
            collector.Regrouped -= _ShowRooms;
            collector.UnresolvedSeen -= _ShowRooms;
        }

        _collectors.Clear();
        _combatEvents.Clear();
        _mate = null;
        OnPropertyChanged(nameof(EnemyObservations));
        _ShowRooms();
    }

    /// <summary>What SAVE stores for one character — empty when nobody ever typed a count for them, the same "seen,
    /// not counted, never stored" rule <see cref="RunEnemyObservationViewModel.IsCounted"/> already applies live.</summary>
    public override void AddToSave(RunSaveDraft draft)
    {
        if (draft.CharacterId is { } characterId && _collectors.TryGetValue(characterId, out RunEnemyObservationCollector? collector))
        {
            draft.Enemies.AddRange(collector.ToInputs());
            draft.Parameters.AddRange(collector.ToRoomParameters());
        }

        // A copy: the save runs off the UI thread, where the live path keeps adding.
        if (draft.CharacterId is { } recordedId && _combatEvents.TryGetValue(recordedId, out List<GameLogEvent>? combatEvents))
        {
            draft.CombatEvents = [.. combatEvents];
        }
    }

    public override void Dispose()
    {
        if (_gamelog is not null)
        {
            _gamelog.CombatObserved -= _OnCombatObserved;
            _gamelog.TelemetryObserved -= _OnTelemetryObserved;
        }
        _roomsSubscription?.Dispose();
        _proposalSubscription?.Dispose();
        base.Dispose();
    }

    private void _Ensure(int characterId)
    {
        _combatEvents.TryAdd(characterId, []);
        if (_collectors.ContainsKey(characterId) || Context.Services.GetService<ISdeAccessor>() is not { } sde)
            return;

        // Rooms find themselves only in an abyssal pocket (ET-368): elsewhere waves and reinforcements look like rooms.
        var collector = new RunEnemyObservationCollector(characterId,
            name => sde.TryGetTypeId(name, out int typeId) ? typeId : AbyssalNpcKnowledge.EnemyTypeIdOf(name),
            Context.RunType.Space is RunSpace.AbyssalPocket
                ? typeId => sde.GetType(typeId) is { } type && AbyssalRoomDetector.IsAbyssalEnemyGroup(type.GroupId)
                : null);
        // Only the summary: re-announcing the list itself while a count is being typed would rebind the editor
        // under the cursor. The rows are an ObservableCollection — the list keeps itself up to date. Wired for
        // every character, not just the one on screen, so a background sibling's count still moves the summary.
        collector.Changed += RefreshSummary;
        collector.Regrouped += _ShowRooms;
        collector.UnresolvedSeen += _ShowRooms;
        collector.RoomsChanged += () => _OnOwnRoomsChanged(characterId);
        collector.Detected += detection => _OnOwnRoomDetected(characterId, detection);
        _collectors[characterId] = collector;
    }

    private void _OnOwnRoomsChanged(int characterId)
    {
        if (characterId == Context.RunCharacterId && Context.IsFleetCommander)
        {
            _AnnounceRooms();
        }
    }

    /// <summary>A member's own find goes to the commander, who decides; it still shows here until his list arrives.</summary>
    private void _OnOwnRoomDetected(int characterId, RoomDetection detection)
    {
        if (characterId != Context.RunCharacterId || Context.IsFleetCommander || Context.FleetId is not { } fleetId
            || Context.GroupCode is not { } groupCode || _bus is null)
        {
            return;
        }

        _ = _bus.PublishAsync(new FleetRunGroupRoomProposedEvent(
            new RunGroupRoomProposal(fleetId, groupCode, detection.AtUtc, detection.Certainty), characterId), EventTarget.Both);
    }

    private void _AnnounceRooms()
    {
        if (Context.FleetId is not { } fleetId || Context.GroupCode is not { } groupCode || _bus is null
            || _OnScreenCollector() is not { } collector)
        {
            return;
        }

        RunGroupRoom[] rooms = [.. collector.RoomBoundaries.Select((atUtc, index) => new RunGroupRoom(atUtc, collector.DetectedCertainties[index]))];
        _ = _bus.PublishAsync(new FleetRunGroupRoomsEvent(new RunGroupRooms(fleetId, groupCode, rooms), Context.RunCharacterId),
            EventTarget.Both);
    }

    /// <summary>The commander's list replaces this pilot's own rooms — only from whoever commands the fleet now, by the
    /// sender the server attached (ET-494, the ET-230 rule). The commander never follows a list.</summary>
    private void _OnCommanderRooms(FleetRunGroupRoomsEvent integrationEvent)
    {
        RunGroupRooms list = integrationEvent.Data;
        if (!string.Equals(list.GroupCode, Context.GroupCode, StringComparison.Ordinal) || Context.IsFleetCommander
            || integrationEvent.CharacterId is not { } sender || sender != Context.FleetCommanderCharacterId)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (RunEnemyObservationCollector collector in _RoomScope())
            {
                collector.Follow([.. list.Rooms.Select(room => (room.AtUtc, room.Certainty))]);
            }
        });
    }

    /// <summary>The commander answers every proposal with his list, so a member's own guess never outlives a no.</summary>
    private void _OnRoomProposed(FleetRunGroupRoomProposedEvent integrationEvent)
    {
        RunGroupRoomProposal proposal = integrationEvent.Data;
        if (!string.Equals(proposal.GroupCode, Context.GroupCode, StringComparison.Ordinal) || !Context.IsFleetCommander
            || integrationEvent.CharacterId is not { } sender || sender == Context.RunCharacterId)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_OnScreenCollector()?.Adopt(proposal.AtUtc, proposal.Certainty) != true)
            {
                _AnnounceRooms();
            }
        });
    }

    private RunEnemyObservationCollector? _OnScreenCollector() =>
        Context.RunCharacterId is { } id ? _collectors.GetValueOrDefault(id) : null;

    private IReadOnlyList<DateTime> _Boundaries() => _mate?.Boundaries ?? _OnScreenCollector()?.RoomBoundaries ?? [];

    private IEnumerable<RunEnemyObservationCollector> _RoomScope() =>
        Context.RunType.ClockPerPilot
            ? _OnScreenCollector() is { } own ? [own] : []
            : _collectors.Values;

    private IReadOnlyList<string> _UnresolvedIn(int? room)
    {
        if (_mate is { } mate)
        {
            return [.. mate.Unresolved.Where(seen => seen.Room == room).Select(seen => seen.Name).Distinct()];
        }

        return _OnScreenCollector() is not { } collector
            ? []
            : [.. collector.UnresolvedSightings.Where(seen => RunRooms.RoomOf(collector.RoomBoundaries, seen.FirstObservedAtUtc) == room)
                .Select(seen => seen.Name).Distinct()];
    }

    // The same faction text as TARGETS, read off the room's rows and its names without a type.
    private string? _FactionText(int room) => TargetsWindowSectionViewModel.FactionText(AbyssalNpcKnowledge.Faction(
        EnemyObservations.Where(observation => observation.RoomNumber == room).Select(observation => observation.EnemyName)
            .Concat(_UnresolvedIn(room))));

    private void _ShowRooms()
    {
        IReadOnlyList<DateTime> boundaries = _Boundaries();
        int roomCount = boundaries.Count == 0 ? 0 : boundaries.Count + 1;
        EnemyRooms =
        [
            .. Enumerable.Range(1, roomCount).Reverse().Select(room => new RunEnemyRoomViewModel(room,
                _RoomWindowText(boundaries, room), isUndoShown: room == roomCount && Context.CanControl && _mate is null,
                [.. EnemyObservations.Where(observation => observation.RoomNumber == room)],
                room > 1 && _mate is null && _OnScreenCollector() is { } collector
                    ? RoomSourceViewModel.Of(collector.DetectedCertainties[room - 2], collector.IsFollowingCommander)
                    : null,
                _FactionText(room), _UnresolvedIn(room)))
        ];
        OnPropertyChanged(nameof(EnemyRooms));
        OnPropertyChanged(nameof(UnresolvedNames));
        OnPropertyChanged(nameof(HasRooms));
        RefreshSummary();
        _ShowLootRooms();
        SightingsChanged?.Invoke();
    }

    private string _RoomWindowText(IReadOnlyList<DateTime> boundaries, int room)
    {
        DateTime? start = room == 1 ? Context.EffectiveStartUtc : RunRooms.StartOf(boundaries, room, default);
        DateTime? end = RunRooms.EndOf(boundaries, room, _mate is null ? Context.EffectiveStopUtc : null);
        return $"{start?.ToLocalTime():HH:mm:ss} – {(end is { } ended ? ended.ToLocalTime().ToString("HH:mm:ss") : "now")}";
    }

    // Loot falls in the room that was going when it was copied (ET-240): every block reads its own pilot's boundaries.
    private void _ShowLootRooms()
    {
        if (Context.LootOverview is not { } overview)
        {
            return;
        }

        foreach (ActivityLootCharacterViewModel block in overview.Characters)
        {
            block.Loot.SetRooms(_collectors.GetValueOrDefault((int)block.CharacterId)?.RoomBoundaries ?? [],
                Context.EffectiveStartUtc, Context.EffectiveStopUtc, isNewestFirst: true);
        }
    }

    // The event fires for damage either way — "250 to Centii Scavenger" and "1 from Centii Servant" alike — and both
    // are the same kind of enemy, so the direction is dropped here rather than carried into the list (ET-115).
    // Routed straight to that character's OWN collector — each already refuses everyone else's id internally, so
    // this only ever widens a row's own observed window, never another character's.
    private void _OnCombatObserved(int characterId, string target, DateTime observedAtUtc, DamageDirection direction)
    {
        if (Context.RunState != ActivityRunState.Running)
            return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            _collectors.GetValueOrDefault(characterId)?.Record(characterId, target, observedAtUtc));
    }

    private void _OnTelemetryObserved(int characterId, GameLogEvent logEvent)
    {
        if (Context.RunState != ActivityRunState.Running)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => _combatEvents.GetValueOrDefault(characterId)?.Add(logEvent));
    }

    /// <summary>A fleet mate's rooms and enemies as last shared (ET-498). The rows cannot be edited: the count is the
    /// mate's, and nothing typed here would be saved anywhere.</summary>
    private sealed record FleetMateRooms(string Name, long UnixMs, IReadOnlyList<DateTime> Boundaries,
        IReadOnlyList<RunEnemyObservationViewModel> Rows, IReadOnlyList<(string Name, int? Room)> Unresolved)
    {
        public static FleetMateRooms From(string name, RunShareUpdate share)
        {
            DateTime sharedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(share.UnixMs).UtcDateTime;
            List<RunEnemyObservationViewModel> rows = [];
            List<(string Name, int? Room)> unresolved = [];
            foreach (RunShareEnemyLine line in share.Enemies)
            {
                if (line.TypeId is { } typeId)
                {
                    rows.Add(new RunEnemyObservationViewModel(typeId, line.Name, sharedAtUtc, line.Room, isEditable: false) { Count = line.Count });
                }
                else
                {
                    unresolved.Add((line.Name, line.Room));
                }
            }

            return new FleetMateRooms(name, share.UnixMs,
                [.. share.RoomStartsUnixMs.Order().Select(unixMs => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime)],
                rows, unresolved);
        }
    }
}
