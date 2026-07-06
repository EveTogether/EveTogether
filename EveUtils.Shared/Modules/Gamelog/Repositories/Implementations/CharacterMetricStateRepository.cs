using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Gamelog.Entities;
using EveUtils.Shared.Modules.Gamelog.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Gamelog.Repositories.Implementations;

/// <summary>Short-lived context per operation. Upsert keyed by the unique character name.</summary>
internal sealed class CharacterMetricStateRepository(IDbContextFactory<SharedDbContext> contextFactory) : ICharacterMetricStateRepository
{
    public async Task<CharacterMetricState?> GetAsync(string characterName, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<CharacterMetricState>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CharacterName == characterName, cancellationToken);
    }

    public async Task UpsertAsync(string characterName, long bountyTotal, int kills, string minedJson, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Set<CharacterMetricState>()
            .FirstOrDefaultAsync(s => s.CharacterName == characterName, cancellationToken);

        if (existing is null)
            db.Set<CharacterMetricState>().Add(new CharacterMetricState
            {
                CharacterName = characterName,
                BountyTotal = bountyTotal,
                Kills = kills,
                MinedJson = minedJson
            });
        else
        {
            existing.BountyTotal = bountyTotal;
            existing.Kills = kills;
            existing.MinedJson = minedJson;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
