using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Killmails.Queries;

[ClientOnly]
internal sealed class GetKillmailsOverviewQueryHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IMarketPriceRepository marketPrices, IFittingRepository fittings)
    : IQueryHandler<GetKillmailsOverviewQuery, Result<IReadOnlyList<KillmailOverviewRowDto>>>
{
    public async Task<Result<IReadOnlyList<KillmailOverviewRowDto>>> Handle(
        GetKillmailsOverviewQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // AsSplitQuery: Items and Attackers are two independent one-to-many collections, and one combined query would
        // cross them — a kill with 60 item rows and 300 attackers would come back as 18,000 rows for that one mail.
        List<LocalKillmail> killmails = await db.Set<LocalKillmail>()
            .AsNoTracking()
            .Include(killmail => killmail.Items)
            .Include(killmail => killmail.Attackers)
            .Where(killmail => killmail.CharacterId == query.CharacterId)
            .OrderByDescending(killmail => killmail.KillmailTimeUtc)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        if (killmails.Count == 0)
        {
            return Result<IReadOnlyList<KillmailOverviewRowDto>>.Success([]);
        }

        HashSet<int> typeIds = [.. killmails.Select(killmail => killmail.VictimShipTypeId),
            .. killmails.SelectMany(killmail => killmail.Items).Select(item => item.TypeId)];
        IReadOnlyDictionary<int, double> prices = await marketPrices.GetAveragePricesAsync(typeIds, cancellationToken);

        List<LocalKillmail> linkedShipLosses = [.. killmails.Where(killmail => killmail.RunId is not null)];
        IReadOnlyList<LinkableRun> runs = killmails.Any(killmail => killmail.IsLoss && killmail.RunId is null)
            ? await _RunsAsync(db, query.CharacterId, cancellationToken)
            : [];
        DateTime nowUtc = DateTime.UtcNow;

        List<KillmailOverviewRowDto> rows = [];
        foreach (LocalKillmail killmail in killmails)
        {
            LocalKillmailAttacker? finalBlow = killmail.Attackers.FirstOrDefault(attacker => attacker.FinalBlow);
            int notLinkedCandidateCount = killmail.IsLoss && killmail.RunId is null
                ? KillmailRunLinker.Match(killmail, runs, linkedShipLosses, nowUtc).CandidateCount
                : 0;

            rows.Add(new KillmailOverviewRowDto(
                killmail.CharacterId, killmail.KillmailId, killmail.KillmailTimeUtc, killmail.SolarSystemId,
                killmail.IsLoss, killmail.VictimShipTypeId, killmail.VictimCharacterId, killmail.VictimCorporationId,
                killmail.VictimAllianceId, killmail.Attackers.Count,
                finalBlow is null
                    ? null
                    : new KillmailFinalBlowDto(finalBlow.AttackerCharacterId, finalBlow.CorporationId, finalBlow.FactionId,
                        finalBlow.ShipTypeId),
                killmail.RunId, killmail.LinkSource, notLinkedCandidateCount, _Value(killmail, prices)));
        }

        return Result<IReadOnlyList<KillmailOverviewRowDto>>.Success(rows);
    }

    // Ship plus every destroyed or dropped item, at today's average — the exact math RunIskFactsReader already uses
    // for a run's own SHIP LOSS share (ET-331): null rather than 0 when none of it is priced (ET-332 AC6).
    private static decimal? _Value(LocalKillmail killmail, IReadOnlyDictionary<int, double> prices) =>
        RunIskFactsReader.KnownLootValue(RunIskFactsReader.LossLines([killmail]), LootKind.Lost, prices);

    // Every run of this character still there to match against — unlike the link pass (LinkKillmailsToRunsCommandHandler),
    // this read is not windowed: it only runs once, for a screen scoped to one character's own killmails.
    private async Task<IReadOnlyList<LinkableRun>> _RunsAsync(ClientDbContext db, long characterId, CancellationToken cancellationToken)
    {
        var runs = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.CharacterId == characterId && !run.DeletedAtUtc.HasValue)
            .Select(run => new
            {
                run.Id, run.CharacterId, run.ActivityKind, run.SolarSystemId, run.StartedAtUtc, run.StoppedAtUtc,
                run.FitContentHash
            })
            .ToListAsync(cancellationToken);

        Dictionary<string, int?> hullByFit = [];
        foreach (string hash in runs.Select(run => run.FitContentHash).OfType<string>().Distinct())
        {
            hullByFit[hash] = (await fittings.FindByContentHashAsync(hash, cancellationToken))?.ShipTypeId;
        }

        return [.. runs.Select(run => new LinkableRun(run.Id, run.CharacterId, run.ActivityKind, run.SolarSystemId,
            run.StartedAtUtc, run.StoppedAtUtc, run.FitContentHash is { } hash ? hullByFit[hash] : null))];
    }
}
