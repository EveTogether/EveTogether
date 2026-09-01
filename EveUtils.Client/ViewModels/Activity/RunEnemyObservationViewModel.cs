using CommunityToolkit.Mvvm.ComponentModel;

namespace EveUtils.Client.ViewModels.Activity;

public sealed partial class RunEnemyObservationViewModel(int enemyTypeId, string enemyName, DateTime observedAtUtc)
    : ObservableObject
{
    public int EnemyTypeId { get; } = enemyTypeId;
    public string EnemyName { get; } = enemyName;

    /// <summary>When this enemy type was first and last seen, over every sighting of it. Stamped as observed rather
    /// than derived from the run's own start and stop, so the stored window is what was witnessed.</summary>
    public DateTime FirstObservedAtUtc { get; private set; } = observedAtUtc;

    public DateTime LastObservedAtUtc { get; private set; } = observedAtUtc;

    /// <summary>Zero is "seen, not counted" and is never stored (ET-106); the list has to show which of the two a
    /// row is, or the player cannot tell what SAVE will keep.</summary>
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
}
