using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Implants.Entities;
using EveUtils.Shared.Modules.Implants.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Implants.Repositories.Implementations;

/// <summary>
/// SQLite-backed store for imported character implants. Client-only — registered explicitly in the client
/// composition (mirrors the character-skill repository), since only the <c>ClientDbContext</c> maps the entity.
/// </summary>
internal sealed class CharacterImplantRepository(IDbContextFactory<SharedDbContext> contextFactory) : ICharacterImplantRepository, ISingletonService
{
    public async Task ReplaceForCharacterAsync(int characterId, IReadOnlyList<int> implantTypeIds, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<CharacterImplant>().Where(i => i.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
        db.Set<CharacterImplant>().AddRange(implantTypeIds.Distinct().Select(typeId =>
            new CharacterImplant { CharacterId = characterId, ImplantTypeId = typeId }));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetTypeIdsAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<CharacterImplant>()
            .Where(i => i.CharacterId == characterId)
            .Select(i => i.ImplantTypeId)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> HasAnyAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<CharacterImplant>().AnyAsync(i => i.CharacterId == characterId, cancellationToken);
    }
}
