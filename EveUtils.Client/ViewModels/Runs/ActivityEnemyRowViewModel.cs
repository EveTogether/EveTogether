using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One sighting of one enemy type on one run. Two runs in the same activity that both saw the same type stay two
/// rows: merging them on type would throw away one of the two first/last windows, and the one that lost the merge
/// would disappear without saying so (ET-160).
/// </summary>
public sealed class ActivityEnemyRowViewModel(RunEnemyObservationDto observation)
{
    public string EnemyName { get; } = observation.EnemyName;

    public string CountText { get; } = observation.Count.ToString();

    /// <summary>The row's own window, which is the reason it is a row of its own.</summary>
    public string WindowText { get; } =
        $"{observation.FirstObservedAtUtc.ToLocalTime():HH:mm:ss} – {observation.LastObservedAtUtc.ToLocalTime():HH:mm:ss}";
}
