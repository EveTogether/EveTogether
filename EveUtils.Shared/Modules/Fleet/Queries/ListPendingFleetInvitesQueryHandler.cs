using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Queries;

internal sealed class ListPendingFleetInvitesQueryHandler(IFleetRepository repository)
    : IQueryHandler<ListPendingFleetInvitesQuery, IReadOnlyList<FleetInvite>>
{
    public Task<IReadOnlyList<FleetInvite>> Handle(ListPendingFleetInvitesQuery query, CancellationToken cancellationToken = default)
        => repository.ListPendingInvitesForFleetAsync(query.FleetId, cancellationToken);
}
