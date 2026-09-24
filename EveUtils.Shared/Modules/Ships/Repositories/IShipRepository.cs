using EveUtils.Shared.Modules.Ships.Entities;

namespace EveUtils.Shared.Modules.Ships.Repositories;

public interface IShipRepository : IShipReader
{
    Task<int> AddAsync(Ship ship, CancellationToken cancellationToken = default);
}
