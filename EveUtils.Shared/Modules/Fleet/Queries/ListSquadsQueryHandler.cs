using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Queries;

internal sealed class ListSquadsQueryHandler(IFleetRepository repository)
    : IQueryHandler<ListSquadsQuery, IReadOnlyList<FleetSquad>>
{
    public Task<IReadOnlyList<FleetSquad>> Handle(ListSquadsQuery query, CancellationToken cancellationToken = default)
        => repository.ListSquadsAsync(query.WingId, cancellationToken);
}
