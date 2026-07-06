using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Fleet.Repositories.Implementations;

/// <summary>
/// SQLite-backed external-character cache. Mirrors the client-store pattern (a short-lived context
/// per call via the factory, no shared context). Upsert is keyed on the ESI character id, so a re-fetch refreshes
/// the single row — including its <see cref="CachedExternalCharacter.FetchedAtUnixMs"/> freshness stamp.
/// </summary>
internal sealed class EfExternalCharacterCache(IDbContextFactory<SharedDbContext> contextFactory) : IExternalCharacterCache, ISingletonService
{
    public async Task<CachedExternalCharacter?> GetAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<CachedExternalCharacter>().AsNoTracking()
            .FirstOrDefaultAsync(c => c.CharacterId == characterId, cancellationToken);
    }

    public async Task UpsertAsync(CachedExternalCharacter entry, CancellationToken cancellationToken = default)
    {
        // Two roster reloads can resolve the same uncached id concurrently. A plain find-then-insert then races —
        // both see no row and both INSERT — and the loser fails with a UNIQUE violation on CharacterId. Update the
        // existing row first; if none exists yet, insert and treat a concurrent-insert UNIQUE violation as "another
        // reload won the insert" by updating instead. Race-safe without a provider-specific upsert statement.
        if (await TryUpdateAsync(entry, cancellationToken))
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
            await TryUpdateAsync(entry, cancellationToken);
        }
    }

    private async Task<bool> TryUpdateAsync(CachedExternalCharacter entry, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Set<CachedExternalCharacter>()
            .FirstOrDefaultAsync(c => c.CharacterId == entry.CharacterId, cancellationToken);
        if (existing is null)
            return false;

        existing.Name = entry.Name;
        existing.Corp = entry.Corp;
        existing.Alliance = entry.Alliance;
        existing.FetchedAtUnixMs = entry.FetchedAtUnixMs;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
