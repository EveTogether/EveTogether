namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>How sure a detected room boundary is (ET-368): <see cref="Sure"/> after a long silence that a second new
/// name confirmed, <see cref="Probable"/> otherwise.</summary>
public enum RoomCertainty
{
    Probable,
    Sure
}

/// <summary>A boundary the detector opened, or — <see cref="IsUpgrade"/> — the last one turning sure.</summary>
public sealed record RoomDetection(DateTime AtUtc, RoomCertainty Certainty, bool IsUpgrade);

/// <summary>
/// Live room detection for one pilot in an abyssal pocket (ET-368). A gate jump leaves no line in the gamelog or the
/// chat log, so the only signal is the enemies: a new room begins at the first sighting of a name that was not in the
/// current room, after at least <see cref="Silence"/> without any abyssal enemy seen. Measured on Raymond's ten runs of
/// 2026-09-18 — derived, not verified against a pressed NEW ROOM.
///
/// Fed abyssal enemies only (SDE groups 1982/1997): a cache or a name without a type is no evidence either way. A
/// sighting older than the newest one fills in the room it belongs to and never opens one.
/// </summary>
public sealed class AbyssalRoomDetector
{
    public const int MaxRooms = 3;
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SureSilence = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(15);

    /// <summary>SDE groups 1982 Abyssal Spaceship Entities and 1997 Abyssal Drone Entities — the Precursor Cache (2009)
    /// sits in every room and is left out on purpose.</summary>
    public static bool IsAbyssalEnemyGroup(int groupId) => groupId is 1982 or 1997;

    private readonly List<HashSet<int>> _rooms = [[]];
    private DateTime? _lastSeenUtc;
    private DateTime? _confirmableSinceUtc;

    public RoomDetection? Observe(int enemyTypeId, DateTime atUtc)
    {
        if (_lastSeenUtc is not { } last || atUtc < last)
        {
            _lastSeenUtc ??= atUtc;
            _rooms[^1].Add(enemyTypeId);
            return null;
        }

        _lastSeenUtc = atUtc;
        TimeSpan gap = atUtc - last;
        bool isNew = !_rooms[^1].Contains(enemyTypeId);
        if (isNew && gap >= Silence && _rooms.Count < MaxRooms)
        {
            _rooms.Add([enemyTypeId]);
            _confirmableSinceUtc = gap >= SureSilence ? atUtc : null;
            return new RoomDetection(atUtc, RoomCertainty.Probable, IsUpgrade: false);
        }

        _rooms[^1].Add(enemyTypeId);
        // A second name the room before never held, close behind the first, is what makes a long silence a gate.
        if (_confirmableSinceUtc is { } since && atUtc - since <= ConfirmWindow && isNew && !_rooms[^2].Contains(enemyTypeId))
        {
            _confirmableSinceUtc = null;
            return new RoomDetection(since, RoomCertainty.Sure, IsUpgrade: true);
        }

        return null;
    }

    /// <summary>The last detected room was taken back: its names fold into the room before, so the same enemies do not
    /// open it again — only a new silence can.</summary>
    public void Undo()
    {
        if (_rooms.Count < 2)
        {
            return;
        }

        _rooms[^2].UnionWith(_rooms[^1]);
        _rooms.RemoveAt(_rooms.Count - 1);
        _confirmableSinceUtc = null;
    }
}
