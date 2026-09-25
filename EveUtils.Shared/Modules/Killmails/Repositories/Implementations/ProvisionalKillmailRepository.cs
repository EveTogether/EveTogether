using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Killmails.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Killmails.Repositories.Implementations;

/// <summary>SQLite-backed provisional-killmail store. Client-only, since only the <c>ClientDbContext</c> maps the entity.</summary>
internal sealed class ProvisionalKillmailRepository(IDbContextFactory<SharedDbContext> contextFactory)
    : IProvisionalKillmailRepository, ISingletonService
{
    public async Task AddAsync(ProvisionalKillmail killmail, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<ProvisionalKillmail>().Add(killmail);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProvisionalKillmail>> GetForCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<ProvisionalKillmail>()
            .AsNoTracking()
            .Where(killmail => killmail.CharacterId == characterId)
            .OrderByDescending(killmail => killmail.KillmailTimeUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> RemoveMatchingAsync(int characterId, DateTime killmailTimeUtc, int victimShipTypeId, string victimName,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<ProvisionalKillmail> matches = await db.Set<ProvisionalKillmail>()
            .Where(killmail => killmail.CharacterId == characterId && killmail.KillmailTimeUtc == killmailTimeUtc
                && killmail.VictimShipTypeId == victimShipTypeId)
            .ToListAsync(cancellationToken);
        matches.RemoveAll(killmail => !string.Equals(killmail.VictimName, victimName, StringComparison.OrdinalIgnoreCase));
        if (matches.Count == 0)
        {
            return false;
        }

        db.Set<ProvisionalKillmail>().RemoveRange(matches);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
