using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Killmails.Queries;

[ClientOnly]
internal sealed class GetKillmailDetailQueryHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IMarketPriceRepository marketPrices, IDispatcher dispatcher)
    : IQueryHandler<GetKillmailDetailQuery, Result<KillmailDetailDto>>
{
    public async Task<Result<KillmailDetailDto>> Handle(GetKillmailDetailQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        LocalKillmail? killmail = await db.Set<LocalKillmail>()
            .AsNoTracking()
            .Include(mail => mail.Items)
            .Include(mail => mail.Attackers)
            .AsSplitQuery()
            .FirstOrDefaultAsync(mail => mail.CharacterId == query.CharacterId && mail.KillmailId == query.KillmailId,
                cancellationToken);
        if (killmail is null)
        {
            return Result<KillmailDetailDto>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The killmail no longer exists.", "Killmails"));
        }

        HashSet<int> typeIds = [killmail.VictimShipTypeId, .. killmail.Items.Select(item => item.TypeId)];
        IReadOnlyDictionary<int, double> prices = await marketPrices.GetAveragePricesAsync(typeIds, cancellationToken);

        decimal? shipValue = prices.TryGetValue(killmail.VictimShipTypeId, out double shipPrice) ? (decimal)shipPrice : null;
        IReadOnlyList<KillmailDetailItemLineDto> items = _Lines(killmail.Items, prices);

        int topDamage = killmail.Attackers.Count == 0 ? 0 : killmail.Attackers.Max(attacker => attacker.DamageDone);
        IReadOnlyList<KillmailDetailAttackerLineDto> attackers = [.. killmail.Attackers
            .OrderBy(attacker => attacker.Ordinal)
            .Select(attacker => new KillmailDetailAttackerLineDto(attacker.Ordinal, attacker.AttackerCharacterId,
                attacker.CorporationId, attacker.AllianceId, attacker.FactionId, attacker.ShipTypeId, attacker.WeaponTypeId,
                attacker.DamageDone, attacker.FinalBlow, attacker.DamageDone == topDamage))];

        KillmailLinkedRunDto? linkedRun = killmail.IsLoss && killmail.RunId is { } runId
            ? await _LinkedRunAsync(db, runId, killmail.KillmailId, killmail.LinkSource, cancellationToken)
            : null;

        return Result<KillmailDetailDto>.Success(new KillmailDetailDto(
            killmail.CharacterId, killmail.KillmailId, killmail.Hash, killmail.KillmailTimeUtc, killmail.SolarSystemId,
            killmail.IsLoss, killmail.VictimShipTypeId, shipValue, killmail.VictimCharacterId, killmail.VictimCorporationId,
            killmail.VictimAllianceId, killmail.DamageTaken, items, attackers, linkedRun));
    }

    // A stack that is partly destroyed and partly dropped becomes two lines (AC1) — each priced on its own quantity,
    // never as a share of a combined one.
    private static IReadOnlyList<KillmailDetailItemLineDto> _Lines(
        IEnumerable<LocalKillmailItem> source, IReadOnlyDictionary<int, double> prices)
    {
        List<KillmailDetailItemLineDto> lines = [];
        foreach (LocalKillmailItem item in source)
        {
            decimal? unitPrice = prices.TryGetValue(item.TypeId, out double price) ? (decimal)price : null;
            if (item.QuantityDestroyed > 0)
            {
                lines.Add(new KillmailDetailItemLineDto(item.Flag, item.TypeId, item.IsNested, true,
                    item.QuantityDestroyed, unitPrice * item.QuantityDestroyed));
            }

            if (item.QuantityDropped > 0)
            {
                lines.Add(new KillmailDetailItemLineDto(item.Flag, item.TypeId, item.IsNested, false,
                    item.QuantityDropped, unitPrice * item.QuantityDropped));
            }
        }

        return lines;
    }

    // The run's own identity (for OPEN RUN) plus the move-to-another-run candidates GetRunLossesQuery already
    // computes (ET-331) — reused rather than re-derived, so this window carries no linking rule of its own (AC6).
    private async Task<KillmailLinkedRunDto?> _LinkedRunAsync(ClientDbContext db, Guid runId, int killmailId,
        KillmailLinkSource linkSource, CancellationToken cancellationToken)
    {
        Run? run = await db.Set<Run>().AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        // The same key the rebuild groups activities by: the group code, or the run itself when it has none.
        ActivitySummary? summary = run.GroupCode is null
            ? await db.Set<ActivitySummary>().AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.RunId == runId, cancellationToken)
            : await db.Set<ActivitySummary>().AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.GroupCode == run.GroupCode, cancellationToken);
        if (summary is null)
        {
            return null;
        }

        Result<IReadOnlyList<Dtos.RunLossDto>> losses = await dispatcher.Query(new GetRunLossesQuery([runId]), cancellationToken);
        IReadOnlyList<Dtos.KillmailRunChoiceDto> otherRuns = losses.IsSuccess
            ? losses.Value?.FirstOrDefault(loss => loss.KillmailId == killmailId)?.OtherRuns ?? []
            : [];

        return new KillmailLinkedRunDto(runId, summary.Id, summary.StartedAtUtc, run.SiteName, run.ActivityKind,
            linkSource, otherRuns);
    }
}
