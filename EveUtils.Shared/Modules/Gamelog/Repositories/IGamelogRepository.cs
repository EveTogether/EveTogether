using EveUtils.Shared.Modules.Gamelog.Entities;

namespace EveUtils.Shared.Modules.Gamelog.Repositories;

public interface IGamelogRepository
{
    Task<int> AddSampleAsync(CombatSample sample, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CombatSample>> RecentAsync(string ownerId, int take, CancellationToken cancellationToken = default);
}
