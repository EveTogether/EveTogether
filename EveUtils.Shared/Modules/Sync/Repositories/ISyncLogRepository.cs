using EveUtils.Shared.Modules.Sync.Entities;

namespace EveUtils.Shared.Modules.Sync.Repositories;

public interface ISyncLogRepository
{
    Task<IReadOnlyList<SyncLog>> ListAsync(CancellationToken cancellationToken = default);

    Task<int> AddAsync(SyncLog syncLog, CancellationToken cancellationToken = default);
}
