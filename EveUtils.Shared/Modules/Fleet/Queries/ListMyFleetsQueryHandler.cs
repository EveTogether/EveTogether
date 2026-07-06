using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Repositories;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Shared.Modules.Fleet.Queries;

internal sealed class ListMyFleetsQueryHandler(IFleetRepository repository)
    : IQueryHandler<ListMyFleetsQuery, IReadOnlyList<FleetEntity>>
{
    public Task<IReadOnlyList<FleetEntity>> Handle(ListMyFleetsQuery query, CancellationToken cancellationToken = default)
        => repository.ListForParticipantAsync(query.CharacterId, cancellationToken);
}
