using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Settings.Entities;
using EveUtils.Shared.Modules.Settings.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Settings.Repositories.Implementations;

internal sealed class SettingRepository(IDbContextFactory<SharedDbContext> contextFactory) : ISettingRepository
{
    public async Task<IReadOnlyList<ClientSetting>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<ClientSetting>().AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task UpsertAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.Set<ClientSetting>().FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (existing is null)
        {
            db.Set<ClientSetting>().Add(new ClientSetting { Key = key, Value = value });
        }
        else
        {
            existing.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
