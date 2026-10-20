using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Client.Runs;

/// <summary>Where the run window's expected earnings come from (ET-516). Two steps, so the panel can show the own runs
/// at once and let a server's figure replace them when it answers.</summary>
public interface IExpectedEarningsSource
{
    Task<ExpectedEarnings> LoadOwnAsync(ActivityStatsKey key, CancellationToken cancellationToken = default);

    /// <summary>The figure of the servers <paramref name="characterId"/> is coupled to and opted in on. A server that
    /// cannot answer is an <see cref="OwnRunsReason"/>, never an error and never a toast.</summary>
    Task<SharedEarningsAttempt> LoadSharedAsync(ActivityStatsKey key, int characterId, CancellationToken cancellationToken = default);
}
