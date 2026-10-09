using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Gamelog;
using EveUtils.Client.ViewModels.Activity;
using CommunityToolkit.Mvvm.Input;
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
/// </summary>
public sealed partial class EnemiesWindowSectionViewModel : RunWindowSection
{
    private readonly GamelogClientService? _gamelog;
    private readonly Dictionary<int, RunEnemyObservationCollector> _collectors = [];
    private readonly Dictionary<int, List<GameLogEvent>> _combatEvents = [];

    public EnemiesWindowSectionViewModel(IRunWindowContext context) : base(context, RunSectionId.Enemies, "ENEMIES")
    {
        _gamelog = context.Services.GetService<GamelogClientService>();
        if (_gamelog is not null)
        {
            _gamelog.CombatObserved += _OnCombatObserved;
            _gamelog.TelemetryObserved += _OnTelemetryObserved;
        }
    }

    /// <summary>The on-screen character's own sightings — whichever run the column is currently showing. Every other
    /// group member's own collector keeps counting in the background regardless (ET-210 round 4).</summary>
    public IReadOnlyList<RunEnemyObservationViewModel> EnemyObservations =>
        Context.RunCharacterId is { } id && _collectors.TryGetValue(id, out RunEnemyObservationCollector? collector)
            ? collector.Observations
            : [];

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
            .. collector.Observations.Select(observation => new TargetSighting(observation.RoomNumber, observation.EnemyName, observation.EnemyTypeId)),
            .. collector.UnresolvedSightings.Select(seen => new TargetSighting(
                RunRooms.RoomOf(collector.RoomBoundaries, seen.FirstObservedAtUtc), seen.Name, null))
        ];

    /// <summary>The on-screen pilot's combat, neut and rep lines of the run, each with its room, for TARGETS. Read only.</summary>
    public IReadOnlyList<(int? Room, GameLogEvent Event)> TargetEvents() =>
        _OnScreenCollector() is not { } collector || !_combatEvents.TryGetValue(Context.RunCharacterId!.Value, out List<GameLogEvent>? events)
            ? []
            : [.. events.Select(logEvent => (RunRooms.RoomOf(collector.RoomBoundaries, logEvent.Timestamp), logEvent))];

    /// <summary>The window's line under the clock while the run has rooms: which room, since when, for how long.</summary>
    public string? CurrentRoomText(DateTime nowUtc)
    {
        if (_OnScreenCollector() is not { RoomBoundaries: { Count: > 0 } boundaries })
        {
            return null;
        }

        DateTime since = boundaries[^1];
        TimeSpan inRoom = TimeSpan.FromTicks(Math.Max(0, ((Context.EffectiveStopUtc ?? nowUtc) - since).Ticks));
        string? faction = _FactionText(boundaries.Count + 1);
        return $"ROOM {boundaries.Count + 1}  since {since.ToLocalTime():HH:mm:ss} · {(int)inRoom.TotalMinutes:00}:{inRoom.Seconds:00}{(faction is null ? string.Empty : $" · {faction}")}";
    }

    /// <summary>Names the SDE has no type for, shown as plain rows while the run has no rooms (ET-369).</summary>
    public IReadOnlyList<string> UnresolvedNames => _UnresolvedIn(null);

    /// <summary>The AUTO badge for the room going on now, when the detector opened it (ET-368).</summary>
    public RoomSourceViewModel? CurrentRoomSource =>
        _OnScreenCollector() is { DetectedCertainties: { Count: > 0 } certainties } ? RoomSourceViewModel.Of(certainties[^1]) : null;

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
        string rooms = _OnScreenCollector() is { RoomBoundaries.Count: > 0 and var boundaries } ? $"{boundaries + 1} rooms · " : string.Empty;
        HeaderSummary = $"{rooms}{types} {(types == 1 ? "type" : "types")} · {(counted == 0 ? "none counted" : $"{counted} counted")}";
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
    /// does live. Lines that are not combat, repair, neut or capacitor are ignored here.</summary>
    internal void RecordCatchUpTelemetry(int characterId, GameLogEvent logEvent)
    {
        if (logEvent is CombatEvent or RemoteRepEvent or NeutEvent or CapTransferEvent)
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
        base.Dispose();
    }

    private void _Ensure(int characterId)
    {
        _combatEvents.TryAdd(characterId, []);
        if (_collectors.ContainsKey(characterId) || Context.Services.GetService<ISdeAccessor>() is not { } sde)
            return;

        // Rooms find themselves only in an abyssal pocket (ET-368): elsewhere waves and reinforcements look like rooms.
        var collector = new RunEnemyObservationCollector(characterId,
            name => sde.TryGetTypeId(name, out int typeId) ? typeId : null,
            Context.RunType.Space is RunSpace.AbyssalPocket
                ? typeId => sde.GetType(typeId) is { } type && AbyssalRoomDetector.IsAbyssalEnemyGroup(type.GroupId)
                : null);
        // Only the summary: re-announcing the list itself while a count is being typed would rebind the editor
        // under the cursor. The rows are an ObservableCollection — the list keeps itself up to date. Wired for
        // every character, not just the one on screen, so a background sibling's count still moves the summary.
        collector.Changed += RefreshSummary;
        collector.Regrouped += _ShowRooms;
        collector.UnresolvedSeen += _ShowRooms;
        _collectors[characterId] = collector;
    }

    private RunEnemyObservationCollector? _OnScreenCollector() =>
        Context.RunCharacterId is { } id ? _collectors.GetValueOrDefault(id) : null;

    private IEnumerable<RunEnemyObservationCollector> _RoomScope() =>
        Context.RunType.ClockPerPilot
            ? _OnScreenCollector() is { } own ? [own] : []
            : _collectors.Values;

    private IReadOnlyList<string> _UnresolvedIn(int? room) => _OnScreenCollector() is not { } collector
        ? []
        : [.. collector.UnresolvedSightings.Where(seen => RunRooms.RoomOf(collector.RoomBoundaries, seen.FirstObservedAtUtc) == room)
            .Select(seen => seen.Name).Distinct()];

    // The same faction text as TARGETS, read off the room's rows and its names without a type.
    private string? _FactionText(int room) => TargetsWindowSectionViewModel.FactionText(AbyssalNpcKnowledge.Faction(
        EnemyObservations.Where(observation => observation.RoomNumber == room).Select(observation => observation.EnemyName)
            .Concat(_UnresolvedIn(room))));

    private void _ShowRooms()
    {
        IReadOnlyList<DateTime> boundaries = _OnScreenCollector()?.RoomBoundaries ?? [];
        int roomCount = boundaries.Count == 0 ? 0 : boundaries.Count + 1;
        EnemyRooms =
        [
            .. Enumerable.Range(1, roomCount).Reverse().Select(room => new RunEnemyRoomViewModel(room,
                _RoomWindowText(boundaries, room), isUndoShown: room == roomCount && Context.CanControl,
                [.. EnemyObservations.Where(observation => observation.RoomNumber == room)],
                RoomSourceViewModel.Of(room > 1 ? _OnScreenCollector()?.DetectedCertainties[room - 2] : null),
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
        DateTime? end = RunRooms.EndOf(boundaries, room, Context.EffectiveStopUtc);
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
}
