using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Implants.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Implants.Repositories.Implementations;

[ClientOnly]
internal sealed class ImplantsCharacterDataEraser(IDbContextFactory<SharedDbContext> contextFactory) : ICharacterDataEraser, ISingletonService
{
    public CharacterDataKind Kind => CharacterDataKind.Cache;

    public async Task EraseAsync(int characterId, string characterName, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<CharacterImplant>().Where(i => i.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
    }
}
