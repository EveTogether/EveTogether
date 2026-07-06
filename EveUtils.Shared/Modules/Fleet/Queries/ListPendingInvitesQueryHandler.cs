using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Queries;

internal sealed class ListPendingInvitesQueryHandler(IFleetRepository repository)
    : IQueryHandler<ListPendingInvitesQuery, IReadOnlyList<FleetInvite>>
{
    public Task<IReadOnlyList<FleetInvite>> Handle(ListPendingInvitesQuery query, CancellationToken cancellationToken = default)
        => repository.ListPendingInvitesForInviteeAsync(query.InviteeCharacterId, cancellationToken);
}
