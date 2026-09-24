using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Killmails.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Killmails.Repositories.Implementations;

[ClientOnly]
internal sealed class KillmailsCharacterDataEraser(IDbContextFactory<SharedDbContext> contextFactory) : ICharacterDataEraser, ISingletonService
{
    public CharacterDataKind Kind => CharacterDataKind.Cache;

    // Children first and by hand: a bulk delete bypasses EF's own cascade, so this does not depend on the database
    // enforcing the foreign keys.
    public async Task EraseAsync(int characterId, string characterName, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<LocalKillmailItem>().Where(i => i.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
        await db.Set<LocalKillmailAttacker>().Where(a => a.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
        await db.Set<LocalKillmail>().Where(k => k.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
    }
}
