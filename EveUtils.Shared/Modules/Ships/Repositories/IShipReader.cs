using EveUtils.Shared.Modules.Ships.Entities;

namespace EveUtils.Shared.Modules.Ships.Repositories;

/// <summary>The read half of <see cref="IShipRepository"/> (ET-383), for everything outside the ship command handlers.</summary>
public interface IShipReader
{
    Task<IReadOnlyList<Ship>> ListAsync(CancellationToken cancellationToken = default);
}
