using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Skills.Entities;
using EveUtils.Shared.Modules.Skills.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Skills.Repositories.Implementations;

/// <summary>
/// SQLite-backed store for a character's training queue. Client-only — registered explicitly in the client
/// composition (mirrors the character-skill repository), since only the <c>ClientDbContext</c> maps the entity.
/// </summary>
internal sealed class CharacterSkillQueueRepository(IDbContextFactory<SharedDbContext> contextFactory) : ICharacterSkillQueueRepository, ISingletonService
{
    public async Task ReplaceForCharacterAsync(int characterId, IReadOnlyList<CharacterSkillQueueEntry> entries, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<CharacterSkillQueueEntry>().Where(e => e.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
        db.Set<CharacterSkillQueueEntry>().AddRange(entries);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CharacterSkillQueueEntry>> GetForCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<CharacterSkillQueueEntry>()
            .AsNoTracking()
            .Where(e => e.CharacterId == characterId)
            .OrderBy(e => e.QueuePosition)
            .ToListAsync(cancellationToken);
    }
}
