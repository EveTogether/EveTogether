using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Repositories;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Shared.Modules.Fleet.Queries;

internal sealed class ListOpenFleetsQueryHandler(IFleetRepository repository)
    : IQueryHandler<ListOpenFleetsQuery, IReadOnlyList<FleetEntity>>
{
    public Task<IReadOnlyList<FleetEntity>> Handle(ListOpenFleetsQuery query, CancellationToken cancellationToken = default)
        => repository.ListOpenAsync(cancellationToken);
}
