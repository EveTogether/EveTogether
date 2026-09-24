using System.Globalization;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fittings.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Fittings.Repositories.Implementations;

/// <summary>The fits imported from this character's ESI fittings. The local library ("0") belongs to nobody and
/// stays.</summary>
[ClientOnly]
internal sealed class FittingsCharacterDataEraser(IDbContextFactory<SharedDbContext> contextFactory) : ICharacterDataEraser, ISingletonService
{
    public CharacterDataKind Kind => CharacterDataKind.History;

    public async Task EraseAsync(int characterId, string characterName, CancellationToken cancellationToken = default)
    {
        var ownerId = characterId.ToString(CultureInfo.InvariantCulture);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Set<LocalFitting>().RemoveRange(
            await db.Set<LocalFitting>().Where(f => f.OwnerId == ownerId).ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }
}
