using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Skills.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Skills.Repositories.Implementations;

[ClientOnly]
internal sealed class SkillsCharacterDataEraser(IDbContextFactory<SharedDbContext> contextFactory) : ICharacterDataEraser, ISingletonService
{
    public CharacterDataKind Kind => CharacterDataKind.Cache;

    public async Task EraseAsync(int characterId, string characterName, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<CharacterSkill>().Where(s => s.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
        await db.Set<CharacterSkillQueueEntry>().Where(e => e.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
        await db.Set<CharacterAttributes>().Where(a => a.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
    }
}
