using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Killmails.Commands;
using EveUtils.Shared.Modules.Killmails.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Client.Runs;

/// <summary>ET-499: a loss that is linked automatically always has a run. One with none lost it when a pulled copy replaced
/// its run, so it is matched again, whatever its age. A loss the pilot unlinked or linked by hand is never reopened: an
/// unlink is a choice and a lost manual link cannot be told from one. Idempotent — a pass leaves each loss linked or
/// unlinked, never in between.</summary>
public sealed class OrphanedRunLinkRepair(IDbContextFactory<ClientDbContext> contextFactory, IDispatcher dispatcher) : IScopedService
{
    public async Task<int> RepairAsync(CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<LocalKillmail> orphaned = db.Set<LocalKillmail>()
            .Where(killmail => killmail.IsLoss && killmail.RunId == null && killmail.LinkSource == KillmailLinkSource.Auto);
        int count = await orphaned.CountAsync(cancellationToken);
        foreach (int characterId in await orphaned.Select(killmail => killmail.CharacterId).Distinct().ToListAsync(cancellationToken))
        {
            await dispatcher.Send(new LinkKillmailsToRunsCommand(characterId, IncludeOlder: true), cancellationToken);
        }

        return count;
    }
}
