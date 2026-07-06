using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Queries;

internal sealed class ListMembersQueryHandler(IFleetRepository repository)
    : IQueryHandler<ListMembersQuery, IReadOnlyList<FleetMember>>
{
    public Task<IReadOnlyList<FleetMember>> Handle(ListMembersQuery query, CancellationToken cancellationToken = default)
        => repository.ListMembersAsync(query.FleetId, cancellationToken);
}
