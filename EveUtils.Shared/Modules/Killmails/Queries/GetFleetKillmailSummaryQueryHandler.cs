using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Killmails.Queries;

[ClientOnly]
internal sealed class GetFleetKillmailSummaryQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetFleetKillmailSummaryQuery, Result<FleetKillmailSummaryDto>>
{
    public async Task<Result<FleetKillmailSummaryDto>> Handle(GetFleetKillmailSummaryQuery query,
        CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Set<LocalKillmail>()
            .AsNoTracking()
            .Where(killmail => query.CharacterIds.Contains(killmail.CharacterId)
                               && killmail.KillmailTimeUtc >= query.ActiveFromUtc
                               && (killmail.SharedFromFleetId == null
                                   || (killmail.SharedFromFleetId == query.FleetId && killmail.SharedFromServer == query.ServerIdentity)))
            .Select(killmail => new { killmail.CharacterId, killmail.KillmailId, killmail.IsLoss, killmail.VictimShipTypeId, killmail.KillmailTimeUtc })
            .ToListAsync(cancellationToken);

        HashSet<int> lossIds = [.. rows.Where(row => row.IsLoss).Select(row => row.KillmailId)];
        // A mail with a fleet member as victim stays a loss, however many other members attacked on it.
        int kills = rows.Where(row => !row.IsLoss && !lossIds.Contains(row.KillmailId)).Select(row => row.KillmailId).Distinct().Count();

        List<FleetMemberKillmailDto> members = [];
        foreach (var group in rows.GroupBy(row => row.CharacterId))
        {
            var lastShipLoss = group
                .Where(row => row.IsLoss && !KillmailRunLinker.IsCapsule(row.VictimShipTypeId))
                .OrderByDescending(row => row.KillmailTimeUtc)
                .Select(row => new FleetMemberShipLossDto(row.KillmailId, row.VictimShipTypeId))
                .FirstOrDefault();
            var pods = group.Where(row => row.IsLoss && KillmailRunLinker.IsCapsule(row.VictimShipTypeId)).ToList();
            members.Add(new FleetMemberKillmailDto(group.Key,
                group.Where(row => !row.IsLoss).Select(row => row.KillmailId).Distinct().Count(), lastShipLoss,
                pods.Select(row => row.KillmailId).Distinct().Count(),
                pods.OrderByDescending(row => row.KillmailTimeUtc).Select(row => (int?)row.KillmailId).FirstOrDefault()));
        }

        return Result<FleetKillmailSummaryDto>.Success(new FleetKillmailSummaryDto(lossIds.Count, kills, members));
    }
}
