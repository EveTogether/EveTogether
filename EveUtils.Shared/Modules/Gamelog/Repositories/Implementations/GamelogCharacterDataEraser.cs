using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Gamelog.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Gamelog.Repositories.Implementations;

[ClientOnly]
internal sealed class GamelogCharacterDataEraser(IDbContextFactory<SharedDbContext> contextFactory) : ICharacterDataEraser, ISingletonService
{
    public CharacterDataKind Kind => CharacterDataKind.Cache;

    // The metric state is keyed by the gamelog's own name, not an id: that is all a gamelog line carries.
    public async Task EraseAsync(int characterId, string characterName, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<CombatSample>().Where(s => s.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
        await db.Set<CharacterMetricState>().Where(m => m.CharacterName == characterName).ExecuteDeleteAsync(cancellationToken);
    }
}
