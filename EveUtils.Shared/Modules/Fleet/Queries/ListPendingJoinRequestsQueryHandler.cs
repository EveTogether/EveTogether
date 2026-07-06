using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Queries;

internal sealed class ListPendingJoinRequestsQueryHandler(IFleetRepository repository)
    : IQueryHandler<ListPendingJoinRequestsQuery, IReadOnlyList<FleetJoinRequest>>
{
    public Task<IReadOnlyList<FleetJoinRequest>> Handle(ListPendingJoinRequestsQuery query, CancellationToken cancellationToken = default)
        => repository.ListPendingJoinRequestsForFleetAsync(query.FleetId, cancellationToken);
}
