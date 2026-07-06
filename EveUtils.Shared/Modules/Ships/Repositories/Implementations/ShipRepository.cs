using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Ships.Entities;
using EveUtils.Shared.Modules.Ships.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Ships.Repositories.Implementations;

/// <summary>
/// Data access via <see cref="IDbContextFactory{TContext}"/> — a short-lived context per
/// operation. Works against <see cref="SharedDbContext"/>, which per host points to the
/// concrete context (client/server).
/// </summary>
internal sealed class ShipRepository(IDbContextFactory<SharedDbContext> contextFactory) : IShipRepository
{
    public async Task<IReadOnlyList<Ship>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<Ship>()
            .AsNoTracking()
            .Include(s => s.Fittings)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> AddAsync(Ship ship, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<Ship>().Add(ship);
        await db.SaveChangesAsync(cancellationToken);
        return ship.Id;
    }
}
