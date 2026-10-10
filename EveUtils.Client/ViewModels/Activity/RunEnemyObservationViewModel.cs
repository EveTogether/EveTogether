using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Activity;

public sealed partial class RunEnemyObservationViewModel(int enemyTypeId, string enemyName, DateTime observedAtUtc,
    int? roomNumber = null, bool isEditable = true) : ObservableObject
{
    /// <summary>False for a fleet mate's row (ET-498): their count, shown, never typed into here.</summary>
    public bool IsEditable { get; } = isEditable;

    public int EnemyTypeId { get; } = enemyTypeId;
    public string EnemyName { get; } = enemyName;

    /// <summary>The room this row was seen in (ET-240) — null until the pilot presses NEW ROOM.</summary>
    public int? RoomNumber { get; internal set; } = roomNumber;

    /// <summary>When this enemy type was first and last seen, over every sighting of it. Stamped as observed rather
    /// than derived from the run's own start and stop, so the stored window is what was witnessed.</summary>
    public DateTime FirstObservedAtUtc { get; private set; } = observedAtUtc;

    public DateTime LastObservedAtUtc { get; private set; } = observedAtUtc;

    /// <summary>Zero means "seen, not counted"; the list has to show which of the two a row is.</summary>
    public bool IsCounted => Count > 0;

    public string CountStateText => IsCounted ? $"{Count} counted" : "not counted";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCounted))]
    [NotifyPropertyChangedFor(nameof(CountStateText))]
    private int _count;

    /// <summary>Widen the window rather than move it: the gamelog is read in chunks and a batch can arrive out of
    /// order, so a later-read line may be an earlier sighting.</summary>
    internal void Observe(DateTime observedAtUtc)
    {
        if (observedAtUtc < FirstObservedAtUtc)
            FirstObservedAtUtc = observedAtUtc;
        if (observedAtUtc > LastObservedAtUtc)
            LastObservedAtUtc = observedAtUtc;
    }

    /// <summary>Fold another row of the same type into this one — an undone room's row back into the room before it.</summary>
    internal void Absorb(RunEnemyObservationViewModel other)
    {
        Observe(other.FirstObservedAtUtc);
        Observe(other.LastObservedAtUtc);
        Count += other.Count;
    }
}
