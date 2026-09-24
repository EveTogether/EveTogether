using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Messaging.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Messaging.Repositories.Implementations;

[ClientOnly]
internal sealed class MessagingCharacterDataEraser(IDbContextFactory<SharedDbContext> contextFactory) : ICharacterDataEraser, ISingletonService
{
    public CharacterDataKind Kind => CharacterDataKind.Cache;

    public async Task EraseAsync(int characterId, string characterName, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Set<ClientInboxMessage>().Where(m => m.RecipientCharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
    }
}
