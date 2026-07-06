using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;

namespace EveUtils.Shared.Modules.Fittings.Queries;

internal sealed class GetFittingsQueryHandler(IFittingRepository repository)
    : IQueryHandler<GetFittingsQuery, IReadOnlyList<LocalFitting>>
{
    public Task<IReadOnlyList<LocalFitting>> Handle(GetFittingsQuery query, CancellationToken cancellationToken = default) =>
        query.OwnerId is null
            ? repository.ListAllAsync(cancellationToken)
            : repository.ListByOwnerAsync(query.OwnerId, cancellationToken);
}
