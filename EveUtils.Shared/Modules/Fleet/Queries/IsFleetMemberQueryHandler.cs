using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet.Queries;

internal sealed class IsFleetMemberQueryHandler(IFleetRepository repository)
    : IQueryHandler<IsFleetMemberQuery, bool>
{
    public Task<bool> Handle(IsFleetMemberQuery query, CancellationToken cancellationToken = default)
        => repository.IsMemberAsync(query.FleetId, query.CharacterId, cancellationToken);
}
