using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Skills.Entities;
using EveUtils.Shared.Modules.Skills.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Skills.Repositories.Implementations;

/// <summary>
/// SQLite-backed store for a character's training attributes. Client-only — registered explicitly in the
/// client composition (mirrors the character-skill repository), since only the <c>ClientDbContext</c> maps the entity.
/// </summary>
internal sealed class CharacterAttributesRepository(IDbContextFactory<SharedDbContext> contextFactory) : ICharacterAttributesRepository, ISingletonService
{
    public async Task ReplaceForCharacterAsync(CharacterAttributes attributes, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<CharacterAttributes>().Where(a => a.CharacterId == attributes.CharacterId).ExecuteDeleteAsync(cancellationToken);
        db.Set<CharacterAttributes>().Add(attributes);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CharacterAttributes?> GetAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<CharacterAttributes>()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.CharacterId == characterId, cancellationToken);
    }
}
