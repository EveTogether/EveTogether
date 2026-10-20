using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Client.Runs;

/// <summary>Where the run window's expected earnings come from (ET-516). Two steps, so the panel can show the own runs
/// at once and let a server's figure replace them when it answers.</summary>
public interface IExpectedEarningsSource
{
    Task<ExpectedEarnings> LoadOwnAsync(ActivityStatsKey key, CancellationToken cancellationToken = default);

    /// <summary>The figure of the servers <paramref name="characterId"/> is coupled to and opted in on. A server that
    /// cannot answer is an <see cref="OwnRunsReason"/>, never an error and never a toast. <paramref name="activityRef"/>
    /// (group code, else run id) names the run the figure is for, when it has one: the server that run belongs to
    /// goes first when several are coupled (ET-512 decision 10).</summary>
    Task<SharedEarningsAttempt> LoadSharedAsync(ActivityStatsKey key, int characterId, CancellationToken cancellationToken = default,
        string? activityRef = null);
}
