using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Killmails.Dtos;
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

    public async Task<(bool Changed, IReadOnlyList<Guid> WithdrawnFromRunIds)> ReconcileFleetShareAsync(int characterId, string serverIdentity, long fleetId, IReadOnlyCollection<int> sharedKillmailIds,
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
                               && killmail.SharedFromServer == serverIdentity
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

    public async Task<IReadOnlyList<(int KillmailId, string Hash)>> GetWithoutPositionAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var missing = await db.Set<LocalKillmail>()
            .AsNoTracking()
            .Where(killmail => killmail.PositionX == null)
            .Select(killmail => new { killmail.KillmailId, killmail.Hash })
            .ToListAsync(cancellationToken);
        return [.. missing.DistinctBy(killmail => killmail.KillmailId).Select(killmail => (killmail.KillmailId, killmail.Hash))];
    }

    public async Task<IReadOnlyList<int>> SetPositionsAsync(IReadOnlyDictionary<int, KillmailPosition> positionsByKillmailId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<int> killmailIds = [.. positionsByKillmailId.Keys];
        List<LocalKillmail> rows = await db.Set<LocalKillmail>()
            .Where(killmail => killmailIds.Contains(killmail.KillmailId) && killmail.PositionX == null)
            .ToListAsync(cancellationToken);
        foreach (LocalKillmail row in rows)
        {
            KillmailPosition position = positionsByKillmailId[row.KillmailId];
            row.PositionX = position.X;
            row.PositionY = position.Y;
            row.PositionZ = position.Z;
        }

        await db.SaveChangesAsync(cancellationToken);
        return [.. rows.Select(row => row.CharacterId).Distinct()];
    }

    public async Task<IReadOnlyList<(int KillmailId, string Hash)>> GetWithoutAttackerSecurityStatusAsync(int limit,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var missing = await db.Set<LocalKillmail>()
            .AsNoTracking()
            .Where(killmail => killmail.Attackers.Any(attacker => attacker.AttackerCharacterId != null && attacker.SecurityStatus == null))
            .OrderBy(killmail => killmail.KillmailId)
            .Select(killmail => new { killmail.KillmailId, killmail.Hash })
            .ToListAsync(cancellationToken);
        return [.. missing.DistinctBy(killmail => killmail.KillmailId).Take(limit).Select(killmail => (killmail.KillmailId, killmail.Hash))];
    }

    public async Task<IReadOnlyList<int>> SetAttackerSecurityStatusesAsync(
        IReadOnlyDictionary<int, IReadOnlyList<KillmailAttackerSecurityStatus>> statusesByKillmailId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<int> killmailIds = [.. statusesByKillmailId.Keys];
        List<LocalKillmailAttacker> rows = await db.Set<LocalKillmailAttacker>()
            .Where(attacker => killmailIds.Contains(attacker.KillmailId) && attacker.SecurityStatus == null)
            .ToListAsync(cancellationToken);

        HashSet<int> changedCharacterIds = [];
        foreach (LocalKillmailAttacker row in rows)
        {
            KillmailAttackerSecurityStatus? status = statusesByKillmailId[row.KillmailId].FirstOrDefault(entry => entry.Ordinal == row.Ordinal);
            if (status is null)
            {
                continue;
            }

            row.SecurityStatus = status.SecurityStatus;
            changedCharacterIds.Add(row.CharacterId);
        }

        await db.SaveChangesAsync(cancellationToken);
        return [.. changedCharacterIds];
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
