using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Queries;

internal sealed class ListMembersActiveElsewhereQueryHandler(IFleetRepository repository)
    : IQueryHandler<ListMembersActiveElsewhereQuery, IReadOnlyList<FleetMemberElsewhereInfo>>
{
    public async Task<IReadOnlyList<FleetMemberElsewhereInfo>> Handle(
        ListMembersActiveElsewhereQuery query, CancellationToken cancellationToken = default)
    {
        var members = await repository.ListMembersAsync(query.FleetId, cancellationToken);
        var elsewhere = new List<FleetMemberElsewhereInfo>();

        foreach (var member in members)
        {
            // An external pilot has no client and no membership anywhere else this server could see; asking about
            // them would be a query per member for an answer that is always "nowhere".
            if (member.IsExternal)
                continue;

            // The earliest activation is the one the character counts for — the same tiebreak FleetBroadcastResolver
            // applies — so the fleet they are "in" is the oldest of their active memberships, not just any other one.
            var actives = await repository.ListActiveMembershipsAsync(member.CharacterId, cancellationToken);
            var counted = actives
                .OrderBy(m => m.ActivatedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(m => m.FleetId)
                .FirstOrDefault();
            if (counted is null || counted.FleetId == query.FleetId)
                continue;

            elsewhere.Add(new FleetMemberElsewhereInfo(member.CharacterId, counted.FleetId, counted.FleetName));
        }

        return elsewhere;
    }
}
