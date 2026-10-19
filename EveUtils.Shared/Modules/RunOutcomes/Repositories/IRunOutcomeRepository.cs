using EveUtils.Shared.Modules.RunOutcomes.Entities;
using EveUtils.Shared.Modules.RunOutcomes.Enums;

namespace EveUtils.Shared.Modules.RunOutcomes.Repositories;

/// <summary>The shared run outcomes: <see cref="IRunOutcomeReader"/> plus the writes, taken only by the run-outcome
/// command handlers.</summary>
public interface IRunOutcomeRepository : IRunOutcomeReader
{
    /// <summary>Stores <paramref name="outcome"/> for its activity, or replaces the stored one when the same contributor
    /// shared it and the revision is newer. On <see cref="RunOutcomeShareStatus.Stored"/> or
    /// <see cref="RunOutcomeShareStatus.Updated"/> the outcome's id is the stored row's.</summary>
    Task<RunOutcomeShareStatus> UpsertAsync(RunOutcome outcome, CancellationToken cancellationToken = default);

    /// <summary>Removes the activity's outcome with its lines, when the contributor shared it and the revision is not
    /// older than the stored one. The removed row's id comes back on <see cref="RunOutcomeShareStatus.Removed"/>.</summary>
    Task<(RunOutcomeShareStatus Status, long? OutcomeId)> RemoveAsync(string activityId, string contributorBucket,
        long revision, CancellationToken cancellationToken = default);

    /// <summary>Removes every outcome the contributor shared, with their lines. Returns how many.</summary>
    Task<int> RemoveAllOfAsync(string contributorBucket, CancellationToken cancellationToken = default);

    /// <summary>Removes every outcome completed before <paramref name="completedBefore"/>, with their lines.</summary>
    Task<int> RemoveCompletedBeforeAsync(DateOnly completedBefore, CancellationToken cancellationToken = default);
}
