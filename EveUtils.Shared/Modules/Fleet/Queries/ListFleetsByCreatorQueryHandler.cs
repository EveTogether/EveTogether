using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Repositories;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Shared.Modules.Fleet.Queries;

internal sealed class ListFleetsByCreatorQueryHandler(IFleetRepository repository)
    : IQueryHandler<ListFleetsByCreatorQuery, IReadOnlyList<FleetEntity>>
{
    public Task<IReadOnlyList<FleetEntity>> Handle(ListFleetsByCreatorQuery query, CancellationToken cancellationToken = default)
        => repository.ListByCreatorAsync(query.CreatorCharacterId, cancellationToken);
}
