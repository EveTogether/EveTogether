using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Skills.Entities;
using EveUtils.Shared.Modules.Skills.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Skills.Repositories.Implementations;

/// <summary>
/// SQLite-backed store for imported character skills. Client-only — registered explicitly in the client
/// composition (mirrors the market-price repository), since only the <c>ClientDbContext</c> maps the entity.
/// </summary>
internal sealed class CharacterSkillRepository(IDbContextFactory<SharedDbContext> contextFactory) : ICharacterSkillRepository, ISingletonService
{
    public async Task ReplaceForCharacterAsync(int characterId, IReadOnlyDictionary<int, int> levels, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<CharacterSkill>().Where(s => s.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
        db.Set<CharacterSkill>().AddRange(levels.Select(kv =>
            new CharacterSkill { CharacterId = characterId, SkillTypeId = kv.Key, Level = kv.Value }));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, int>> GetLevelsAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<CharacterSkill>()
            .Where(s => s.CharacterId == characterId)
            .ToDictionaryAsync(s => s.SkillTypeId, s => s.Level, cancellationToken);
    }

    public async Task<bool> HasAnyAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<CharacterSkill>().AnyAsync(s => s.CharacterId == characterId, cancellationToken);
    }
}
