using EveUtils.Shared.Modules.Runs.Entities;

namespace EveUtils.Shared.Modules.Runs.Repositories;

public interface IRunSyncRepository
{
    Task<DateTime?> UpsertAsync(Run run, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Run>> ListChangedAsync(long characterId, IReadOnlyCollection<string> groupCodes, DateTime sinceUtc,
        CancellationToken cancellationToken = default);
}
