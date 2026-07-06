using EveUtils.Shared.Modules.Ships.Entities;

namespace EveUtils.Shared.Modules.Ships.Repositories;

public interface IShipRepository
{
    Task<IReadOnlyList<Ship>> ListAsync(CancellationToken cancellationToken = default);

    Task<int> AddAsync(Ship ship, CancellationToken cancellationToken = default);
}
