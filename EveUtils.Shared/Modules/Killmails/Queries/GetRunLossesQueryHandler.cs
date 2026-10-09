using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Isk;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Killmails.Queries;

[ClientOnly]
internal sealed class GetRunLossesQueryHandler(IDbContextFactory<ClientDbContext> contextFactory, IMarketPriceRepository marketPrices)
    : IQueryHandler<GetRunLossesQuery, Result<IReadOnlyList<RunLossDto>>>
{
    public async Task<Result<IReadOnlyList<RunLossDto>>> Handle(GetRunLossesQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<LocalKillmail> losses = await db.Set<LocalKillmail>()
            .AsNoTracking()
            .Include(killmail => killmail.Attackers.Where(attacker => attacker.FinalBlow))
            .Where(killmail => killmail.IsLoss && killmail.RunId != null && query.RunIds.Contains(killmail.RunId.Value))
            .OrderBy(killmail => killmail.KillmailTimeUtc)
            .ToListAsync(cancellationToken);

        // The same fixed-else-live pricing the stored LINKED LOSS share is added up with, so the rows sum to it.
        List<LinkedLoss> priced = [.. (await RunIskFactsReader.LinkedLossesAsync(db, query.RunIds, cancellationToken))
            .SelectMany(group => group)];
        IReadOnlyDictionary<int, double> live = await marketPrices.GetAveragePricesAsync(
            [.. RunIskFactsReader.LossLines(priced.Select(loss => loss.Killmail)).Select(line => line.ItemTypeId).Distinct()],
            cancellationToken);

        List<RunLossDto> dtos = [];
        foreach (LocalKillmail loss in losses)
        {
            Guid runId = loss.RunId.GetValueOrDefault();
            LocalKillmailAttacker? finalBlow = loss.Attackers.FirstOrDefault();
            dtos.Add(new RunLossDto(loss.CharacterId, loss.KillmailId, runId, loss.KillmailTimeUtc, loss.VictimShipTypeId,
                loss.LinkSource,
                finalBlow is null
                    ? null
                    : new KillmailFinalBlowDto(finalBlow.AttackerCharacterId, finalBlow.CorporationId, finalBlow.FactionId,
                        finalBlow.ShipTypeId),
                await _OtherRunsAsync(db, loss, runId, cancellationToken),
                priced.FirstOrDefault(candidate => candidate.Killmail.CharacterId == loss.CharacterId
                                                   && candidate.Killmail.KillmailId == loss.KillmailId) is { } linked
                    ? RunIskFactsReader.LossValue([linked], live)
                    : null));
        }

        return Result<IReadOnlyList<RunLossDto>>.Success(dtos);
    }

    // The same time window the link rule starts from, without its place and hull: the pilot knows better than either.
    private static async Task<IReadOnlyList<KillmailRunChoiceDto>> _OtherRunsAsync(ClientDbContext db, LocalKillmail loss,
        Guid runId, CancellationToken cancellationToken)
    {
        DateTime stoppedFromUtc = loss.KillmailTimeUtc - KillmailRunLinker.StopGrace;
        return await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.CharacterId == loss.CharacterId && run.Id != runId && !run.DeletedAtUtc.HasValue
                          && run.StartedAtUtc <= loss.KillmailTimeUtc
                          && (run.StoppedAtUtc == null || run.StoppedAtUtc >= stoppedFromUtc))
            .OrderBy(run => run.StartedAtUtc)
            .Select(run => new KillmailRunChoiceDto(run.Id, run.SiteName, run.StartedAtUtc))
            .ToListAsync(cancellationToken);
    }
}
