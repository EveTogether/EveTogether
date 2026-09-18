using EveUtils.Shared.Modules.Runs.Entities;

namespace EveUtils.Shared.Modules.Runs.Repositories;

public interface IRunSyncRepository
{
    Task<DateTime?> UpsertAsync(Run run, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Run>> ListChangedAsync(long characterId, IReadOnlyCollection<string> groupCodes, DateTime sinceUtc,
        CancellationToken cancellationToken = default);

    /// <summary>The characters holding a run in <paramref name="groupCode"/> that is not deleted — the same rule
    /// <see cref="ListChangedAsync"/> uses to decide who may pull that group, so a notice goes to nobody the pull
    /// would then refuse.</summary>
    Task<IReadOnlyList<long>> ListGroupHoldersAsync(string groupCode, CancellationToken cancellationToken = default);

    /// <summary>The saved, non-deleted runs started in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>) that
    /// <paramref name="characterId"/> may see (ET-311): its own, and its group mates' under the same holder rule as
    /// <see cref="ListChangedAsync"/>.</summary>
    Task<IReadOnlyList<Run>> ListPublishedAsync(long characterId, DateTime fromUtc, DateTime toUtc,
        CancellationToken cancellationToken = default);
}
