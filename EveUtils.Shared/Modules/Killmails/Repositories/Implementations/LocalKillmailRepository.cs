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

    public async Task<IReadOnlyList<LocalKillmail>> AddMissingAsync(int characterId, IReadOnlyList<LocalKillmail> killmails, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var known = await _KnownIdsAsync(db, characterId, killmails.Select(killmail => killmail.KillmailId).ToList(), cancellationToken);
        List<LocalKillmail> added = [.. killmails.Where(killmail => !known.Contains(killmail.KillmailId))];
        db.Set<LocalKillmail>().AddRange(added);
        await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    public async Task<(bool Changed, IReadOnlyList<Guid> WithdrawnFromRunIds)> ReconcileFleetShareAsync(int characterId, long fleetId, IReadOnlyCollection<int> sharedKillmailIds,
        IReadOnlyList<LocalKillmail> fetched, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var known = await _KnownIdsAsync(db, characterId, fetched.Select(killmail => killmail.KillmailId).ToList(), cancellationToken);
        db.Set<LocalKillmail>().AddRange(fetched.Where(killmail => !known.Contains(killmail.KillmailId)));

        // ponytail: one fleet per row; a mail shared in two fleets returns on the other's next share. Per-fleet table if needed.
        // Items and attackers are loaded so EF removes them too.
        List<LocalKillmail> withdrawn = await db.Set<LocalKillmail>()
            .Include(killmail => killmail.Items)
            .Include(killmail => killmail.Attackers)
            .Where(killmail => killmail.CharacterId == characterId && killmail.SharedFromFleetId == fleetId
                               && !sharedKillmailIds.Contains(killmail.KillmailId))
            .ToListAsync(cancellationToken);
        db.Set<LocalKillmail>().RemoveRange(withdrawn);
        bool changed = await db.SaveChangesAsync(cancellationToken) > 0;
        return (changed, [.. withdrawn.Select(killmail => killmail.RunId).OfType<Guid>().Distinct()]);
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
