using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Repositories;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Shared.Modules.Fleet.Queries;

internal sealed class GetFleetQueryHandler(IFleetRepository repository)
    : IQueryHandler<GetFleetQuery, FleetEntity?>
{
    public Task<FleetEntity?> Handle(GetFleetQuery query, CancellationToken cancellationToken = default)
        => repository.GetAsync(query.FleetId, cancellationToken);
}
