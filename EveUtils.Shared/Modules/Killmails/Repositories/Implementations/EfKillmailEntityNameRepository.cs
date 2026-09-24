using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Killmails.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Killmails.Repositories.Implementations;

/// <summary>SQLite-backed killmail entity name cache. Client-only, since only the <c>ClientDbContext</c> maps the
/// entity. Mirrors <c>EfExternalCharacterCache</c>'s update-then-insert upsert so two concurrent hydrations of the
/// same id (e.g. two open killmail windows) never race into a UNIQUE-violation.</summary>
internal sealed class EfKillmailEntityNameRepository(IDbContextFactory<SharedDbContext> contextFactory)
    : IKillmailEntityNameRepository, ISingletonService
{
    public async Task<IReadOnlyDictionary<long, KillmailEntityName>> GetManyAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Set<KillmailEntityName>()
            .AsNoTracking()
            .Where(entity => ids.Contains(entity.Id))
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(entity => entity.Id);
    }

    public async Task UpsertAsync(KillmailEntityName entry, CancellationToken cancellationToken = default)
    {
        if (await _TryUpdateAsync(entry, cancellationToken))
            return;

        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            db.Add(entry);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent UpsertAsync inserted the row between the update probe above and this insert — update it.
            await _TryUpdateAsync(entry, cancellationToken);
        }
    }

    private async Task<bool> _TryUpdateAsync(KillmailEntityName entry, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Set<KillmailEntityName>().FirstOrDefaultAsync(entity => entity.Id == entry.Id, cancellationToken);
        if (existing is null)
            return false;

        existing.Kind = entry.Kind;
        existing.Name = entry.Name;
        existing.RefreshedAtUtc = entry.RefreshedAtUtc;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
