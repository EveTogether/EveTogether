using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Killmails.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Killmails.Repositories.Implementations;

/// <summary>SQLite-backed killmail store. Client-only, since only the <c>ClientDbContext</c> maps the entities.</summary>
internal sealed class LocalKillmailRepository(IDbContextFactory<SharedDbContext> contextFactory) : ILocalKillmailRepository, ISingletonService
{
    public async Task<IReadOnlySet<int>> GetKnownIdsAsync(int characterId, IReadOnlyCollection<int> killmailIds, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await _KnownIdsAsync(db, characterId, killmailIds, cancellationToken);
    }

    public async Task AddMissingAsync(int characterId, IReadOnlyList<LocalKillmail> killmails, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var known = await _KnownIdsAsync(db, characterId, killmails.Select(killmail => killmail.KillmailId).ToList(), cancellationToken);
        db.Set<LocalKillmail>().AddRange(killmails.Where(killmail => !known.Contains(killmail.KillmailId)));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LocalKillmail>> GetForCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<LocalKillmail>()
            .AsNoTracking()
            .Include(killmail => killmail.Items)
            .Include(killmail => killmail.Attackers.OrderBy(attacker => attacker.Ordinal))
            .Where(killmail => killmail.CharacterId == characterId)
            .OrderByDescending(killmail => killmail.KillmailTimeUtc)
            .ToListAsync(cancellationToken);
    }

    private static async Task<IReadOnlySet<int>> _KnownIdsAsync(SharedDbContext db, int characterId, IReadOnlyCollection<int> killmailIds,
        CancellationToken cancellationToken)
    {
        var known = await db.Set<LocalKillmail>()
            .Where(killmail => killmail.CharacterId == characterId && killmailIds.Contains(killmail.KillmailId))
            .Select(killmail => killmail.KillmailId)
            .ToListAsync(cancellationToken);
        return known.ToHashSet();
    }
}
