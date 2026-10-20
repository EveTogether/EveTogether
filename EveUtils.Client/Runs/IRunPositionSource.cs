namespace EveUtils.Client.Runs;

/// <summary>Where a saved run's position in its activity is read from (ET-520): the run detail and the runs overview's
/// drawer ask the same source.</summary>
public interface IRunPositionSource
{
    /// <param name="activityRef">The saved activity's group code, else its run id.</param>
    /// <param name="characterIds">The pilot's own characters: whichever of them is connected to a server asks it.</param>
    /// <returns>Null for an activity with no statistic key (mining, a free run, an unnamed site).</returns>
    Task<RunPosition?> ReadAsync(string activityRef, IReadOnlyList<int> characterIds, CancellationToken cancellationToken = default);
}
