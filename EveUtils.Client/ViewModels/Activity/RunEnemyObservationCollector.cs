using System.Collections.ObjectModel;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Activity;

/// <param name="isAbyssalEnemy">Set only in an abyssal pocket (ET-368): which types the room detector reads. Null
/// keeps rooms to NEW ROOM alone, the way every other run type has them.</param>
public sealed class RunEnemyObservationCollector(int characterId, Func<string, int?> typeIdResolver,
    Func<int, bool>? isAbyssalEnemy = null)
{
    private readonly List<DateTime> _roomBoundaries = [];
    // Per boundary: how sure the detector was, or null for one the pilot set (ET-368).
    private readonly List<RoomCertainty?> _detectedCertainties = [];
    private AbyssalRoomDetector? _detector = isAbyssalEnemy is null ? null : new AbyssalRoomDetector();

    public ObservableCollection<RunEnemyObservationViewModel> Observations { get; } = [];

    /// <summary>Where the pilot pressed NEW ROOM (ET-240), oldest first — empty while the run has no rooms.</summary>
    public IReadOnlyList<DateTime> RoomBoundaries => _roomBoundaries;

    /// <summary>Per boundary in <see cref="RoomBoundaries"/>: the detector's certainty, or null when the pilot set it.</summary>
    public IReadOnlyList<RoomCertainty?> DetectedCertainties => _detectedCertainties;

    /// <summary>A row appeared or moved between rooms — the moments a list grouped per room has to be laid out
    /// again. Not raised for a typed count, so an open editor is never rebound under the cursor.</summary>
    public event Action? Regrouped;

    /// <summary>Raised when a row appears or its count changes — a header summary is only true if it is recomputed
    /// then, and nothing else tells the window a count was typed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Record one observed enemy.
    ///
    /// <paramref name="observedAtUtc"/> is the gamelog line's own time, and there is deliberately no overload that
    /// defaults it to "now": EVE flushes its log in chunks, so a single poll can carry several seconds of combat,
    /// and stamping the batch with the read time would file it all at one instant. A time nobody measured is worse
    /// than no time at all.
    ///
    /// An enemy is keyed on <em>type alone</em>. The question this list answers is which kind of enemy and how many;
    /// which way the damage went is not part of it, so a rat you shot and a rat that shot you are one row whose
    /// window spans both sightings (ET-115). Nothing is added up here either: the count stays the player's.
    ///
    /// Once the run has rooms the key is type and room (ET-240), the room read off the line's own time — so a
    /// sighting EVE flushes after NEW ROOM still lands in the room it happened in.
    /// </summary>
    public void Record(int observedCharacterId, string target, DateTime observedAtUtc)
    {
        if (observedCharacterId != characterId || typeIdResolver(target) is not int enemyTypeId)
            return;

        if (_detector is not null && isAbyssalEnemy?.Invoke(enemyTypeId) == true
            && _detector.Observe(enemyTypeId, observedAtUtc) is { } detection)
        {
            _ApplyDetection(detection);
        }

        int? room = RunRooms.RoomOf(_roomBoundaries, observedAtUtc);
        if (Observations.FirstOrDefault(observation => observation.EnemyTypeId == enemyTypeId && observation.RoomNumber == room)
            is { } seen)
        {
            seen.Observe(observedAtUtc);
            return;
        }

        var added = new RunEnemyObservationViewModel(enemyTypeId, target, observedAtUtc, room);
        added.PropertyChanged += (_, _) => Changed?.Invoke();
        Observations.Add(added);
        Regrouped?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Close the current room at <paramref name="atUtc"/> and begin the next. The first press makes two
    /// rooms: everything seen so far becomes room 1. A moment not after the last boundary is refused.
    ///
    /// The pilot's own press wins over the detector for the rest of the run (ET-368, the ET-262 rule): every boundary
    /// it set becomes the pilot's, since they were seen and flown on, and the detector stops.</summary>
    public bool StartRoom(DateTime atUtc)
    {
        if (_roomBoundaries.Count > 0 && atUtc <= _roomBoundaries[^1])
        {
            return false;
        }

        _detector = null;
        for (int index = 0; index < _detectedCertainties.Count; index++)
        {
            _detectedCertainties[index] = null;
        }

        _AddBoundary(atUtc, certainty: null);
        return true;
    }

    private void _ApplyDetection(RoomDetection detection)
    {
        if (detection.IsUpgrade)
        {
            _detectedCertainties[^1] = detection.Certainty;
            Changed?.Invoke();
            return;
        }

        _AddBoundary(detection.AtUtc, detection.Certainty);
    }

    private void _AddBoundary(DateTime atUtc, RoomCertainty? certainty)
    {
        if (_roomBoundaries.Count == 0)
        {
            foreach (RunEnemyObservationViewModel observation in Observations)
            {
                observation.RoomNumber = 1;
            }
        }

        _roomBoundaries.Add(atUtc);
        _detectedCertainties.Add(certainty);
        Regrouped?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Take the last boundary back: the last room's rows fold into the room before it per type — counts
    /// added, windows widened. Undoing the only boundary leaves a run without rooms, as if NEW ROOM was never pressed.</summary>
    public bool UndoRoom()
    {
        if (_roomBoundaries.Count == 0)
        {
            return false;
        }

        int last = _roomBoundaries.Count + 1;
        if (_detectedCertainties[^1] is not null)
        {
            _detector?.Undo();
        }

        _roomBoundaries.RemoveAt(_roomBoundaries.Count - 1);
        _detectedCertainties.RemoveAt(_detectedCertainties.Count - 1);
        int? into = _roomBoundaries.Count == 0 ? null : last - 1;
        if (into is null)
        {
            foreach (RunEnemyObservationViewModel observation in Observations.Where(observation => observation.RoomNumber != last))
            {
                observation.RoomNumber = null;
            }
        }

        foreach (RunEnemyObservationViewModel undone in Observations.Where(observation => observation.RoomNumber == last).ToList())
        {
            if (Observations.FirstOrDefault(kept => kept.EnemyTypeId == undone.EnemyTypeId && kept.RoomNumber == into) is { } kept)
            {
                kept.Absorb(undone);
                Observations.Remove(undone);
            }
            else
            {
                undone.RoomNumber = into;
            }
        }

        Regrouped?.Invoke();
        Changed?.Invoke();
        return true;
    }

    /// <summary>The boundaries as SAVE stores them: the pilot's as <see cref="RunParameterKey.RoomStarted"/>, the
    /// detector's as <see cref="RunParameterKey.RoomDetected"/> (ET-368).</summary>
    public IReadOnlyList<RunParameterInput> ToRoomParameters() =>
    [
        .. _roomBoundaries.Select((boundary, index) => new RunParameterInput
        {
            ParameterKey = _detectedCertainties[index] is null ? RunParameterKey.RoomStarted : RunParameterKey.RoomDetected,
            TypedValue = string.Empty,
            ObservedAtUtc = boundary
        })
    ];

    /// <summary>The seam ET-106 left to ET-105: what the window watched, in the shape <c>SaveRunCommand</c> stores.</summary>
    public IReadOnlyList<RunEnemyObservationInput> ToInputs() =>
    [
        .. Observations.Select(observation => new RunEnemyObservationInput
        {
            Count = observation.Count,
            EnemyTypeId = observation.EnemyTypeId,
            EnemyName = observation.EnemyName,
            FirstObservedAtUtc = observation.FirstObservedAtUtc,
            LastObservedAtUtc = observation.LastObservedAtUtc,
            RoomNumber = observation.RoomNumber
        })
    ];
}
