using System.Text.Json;
using EveUtils.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Identity;

/// <summary>
/// SQLite-backed character registry — replaces the JSON-file <c>InMemoryCharacterRegistry</c>.
/// Stores each known local character in the client DB (Character table) via the shared DbContext factory
/// (on the client that resolves to the ClientDbContext). No "active character" concept.
/// </summary>
public sealed class EfCharacterRegistry(IDbContextFactory<SharedDbContext> contextFactory) : ICharacterRegistry
{
    public event Action RegistryChanged = () => { };

    public async Task AddOrUpdateAsync(Character character, CancellationToken cancellationToken = default)
    {
        if (character.EsiCharacterId is null)
            throw new ArgumentException("Character must have an EsiCharacterId to be registered.", nameof(character));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var id = character.EsiCharacterId.Value;
        var existing = await db.Set<LocalCharacter>().FirstOrDefaultAsync(c => c.EsiCharacterId == id, cancellationToken);
        var scopesJson = JsonSerializer.Serialize(character.GrantedScopes ?? []);

        if (existing is null)
        {
            // Append a brand-new character to the end of the user-defined order.
            var nextOrder = await db.Set<LocalCharacter>().AnyAsync(cancellationToken)
                ? await db.Set<LocalCharacter>().MaxAsync(c => c.SortOrder, cancellationToken) + 1
                : 0;
            db.Set<LocalCharacter>().Add(new LocalCharacter
            {
                EsiCharacterId = id, Name = character.Name, GrantedScopesJson = scopesJson, SortOrder = nextOrder
            });
        }
        else
        {
            existing.Name = character.Name;
            existing.GrantedScopesJson = scopesJson; // SortOrder left untouched — keep the user's position
        }

        await db.SaveChangesAsync(cancellationToken);
        RegistryChanged();
    }

    public async Task<IReadOnlyList<Character>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Set<LocalCharacter>().AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(cancellationToken);
        return rows.Select(Map).ToList();
    }

    public async Task ReorderAsync(IReadOnlyList<int> orderedEsiCharacterIds, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Set<LocalCharacter>().ToListAsync(cancellationToken);

        // Listed characters take positions 0..n-1 in the given order; any unlisted character keeps a stable position
        // after them (ordered by its current SortOrder so the tail does not shuffle).
        var rank = orderedEsiCharacterIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);
        var tail = orderedEsiCharacterIds.Count;
        foreach (var row in rows.OrderBy(r => r.SortOrder))
            row.SortOrder = rank.TryGetValue(row.EsiCharacterId, out var index) ? index : tail++;

        await db.SaveChangesAsync(cancellationToken);
        // No RegistryChanged(): a reorder is not a membership change, and the caller already reflects the new order.
    }

    public async Task RemoveAsync(int esiCharacterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Set<LocalCharacter>().FirstOrDefaultAsync(c => c.EsiCharacterId == esiCharacterId, cancellationToken);
        if (existing is null) return;

        db.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
        RegistryChanged();
    }

    private static Character Map(LocalCharacter e)
    {
        var scopes = JsonSerializer.Deserialize<List<string>>(e.GrantedScopesJson) ?? [];
        return new Character(e.Name, e.EsiCharacterId, scopes);
    }
}
