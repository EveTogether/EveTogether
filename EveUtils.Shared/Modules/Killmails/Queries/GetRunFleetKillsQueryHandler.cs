using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Dtos;
using EveUtils.Shared.Modules.Killmails.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Killmails.Queries;

[ClientOnly]
internal sealed class GetRunFleetKillsQueryHandler(IDbContextFactory<ClientDbContext> contextFactory, IDispatcher dispatcher)
    : IQueryHandler<GetRunFleetKillsQuery, Result<IReadOnlyList<RunFleetKillDto>>>
{
    public async Task<Result<IReadOnlyList<RunFleetKillDto>>> Handle(GetRunFleetKillsQuery query,
        CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // The same upper bound the killmail-run linker uses: a mail up to its stop grace after a run stopped still belongs to it.
        DateTime? untilUtc = query.StoppedUtc + KillmailRunLinker.StopGrace;
        List<LocalKillmail> rows = await db.Set<LocalKillmail>()
            .AsNoTracking()
            .Include(killmail => killmail.Attackers.Where(attacker => attacker.FinalBlow))
            .Where(killmail => query.CharacterIds.Contains(killmail.CharacterId)
                               && killmail.KillmailTimeUtc >= query.StartedUtc
                               && (untilUtc == null || killmail.KillmailTimeUtc <= untilUtc))
            .ToListAsync(cancellationToken);

        // A mail one member lost stays a loss, however many other members attacked on it, and even when the victim's own row is not stored.
        HashSet<int> lossIds = [.. rows.Where(row => row.IsLoss).Select(row => row.KillmailId)];
        List<IGrouping<int, LocalKillmail>> kills = [.. rows
            .Where(row => !row.IsLoss && !lossIds.Contains(row.KillmailId)
                          && !(row.VictimCharacterId is { } victim && query.CharacterIds.Contains(victim)))
            .GroupBy(row => row.KillmailId)];

        List<RunFleetKillDto> dtos = [];
        foreach (IGrouping<int, LocalKillmail> kill in kills)
        {
            LocalKillmail first = kill.OrderBy(row => row.CharacterId).First();
            LocalKillmailAttacker? finalBlow = kill.SelectMany(row => row.Attackers).FirstOrDefault();
            // One value per mail, from the detail's own read, so the section can never price it differently.
            Result<KillmailDetailDto> detail = await dispatcher.Query(
                new GetKillmailDetailQuery(first.CharacterId, first.KillmailId), cancellationToken);
            dtos.Add(new RunFleetKillDto(first.KillmailId, first.CharacterId, first.KillmailTimeUtc, first.VictimShipTypeId, false,
                first.VictimCharacterId, first.VictimCorporationId, [.. kill.Select(row => row.CharacterId).Order()],
                finalBlow is null
                    ? null
                    : new KillmailFinalBlowDto(finalBlow.AttackerCharacterId, finalBlow.CorporationId, finalBlow.FactionId,
                        finalBlow.ShipTypeId),
                detail.IsSuccess ? detail.Value?.DestroyedValue : null));
        }

        return Result<IReadOnlyList<RunFleetKillDto>>.Success(_FoldPods(dtos));
    }

    // A victim's capsule within the capsule grace after its ship is that ship's pod, not a kill of its own (ET-372's "+ pod").
    private static IReadOnlyList<RunFleetKillDto> _FoldPods(List<RunFleetKillDto> kills)
    {
        List<RunFleetKillDto> ships = [.. kills.Where(kill => !KillmailRunLinker.IsCapsule(kill.VictimShipTypeId))];
        HashSet<int> folded = [];
        for (int index = 0; index < ships.Count; index++)
        {
            RunFleetKillDto ship = ships[index];
            RunFleetKillDto? pod = ship.VictimCharacterId is null
                ? null
                : kills.FirstOrDefault(kill => KillmailRunLinker.IsCapsule(kill.VictimShipTypeId)
                                               && kill.VictimCharacterId == ship.VictimCharacterId
                                               && kill.KillmailTimeUtc >= ship.KillmailTimeUtc
                                               && kill.KillmailTimeUtc - ship.KillmailTimeUtc <= KillmailRunLinker.CapsuleGrace);
            if (pod is not null && folded.Add(pod.KillmailId))
            {
                ships[index] = ship with { HasPod = true, DestroyedValue = _Sum(ship.DestroyedValue, pod.DestroyedValue) };
            }
        }

        return [.. kills.Where(kill => !folded.Contains(kill.KillmailId)).OrderBy(kill => kill.KillmailTimeUtc)
            .Select(kill => ships.FirstOrDefault(ship => ship.KillmailId == kill.KillmailId) ?? kill)];
    }

    private static decimal? _Sum(decimal? left, decimal? right) =>
        left is null && right is null ? null : left.GetValueOrDefault() + right.GetValueOrDefault();
}
