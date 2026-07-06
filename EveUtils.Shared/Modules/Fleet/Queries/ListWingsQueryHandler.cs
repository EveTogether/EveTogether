using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Queries;

internal sealed class ListWingsQueryHandler(IFleetRepository repository)
    : IQueryHandler<ListWingsQuery, IReadOnlyList<FleetWing>>
{
    public Task<IReadOnlyList<FleetWing>> Handle(ListWingsQuery query, CancellationToken cancellationToken = default)
        => repository.ListWingsAsync(query.FleetId, cancellationToken);
}
