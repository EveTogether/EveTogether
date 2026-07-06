using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;

namespace EveUtils.Shared.Modules.Fittings.Queries;

internal sealed class GetSharedFittingsQueryHandler(ISharedFitRepository repository)
    : IQueryHandler<GetSharedFittingsQuery, IReadOnlyList<SharedFit>>
{
    public Task<IReadOnlyList<SharedFit>> Handle(GetSharedFittingsQuery query, CancellationToken cancellationToken = default) =>
        repository.ListAsync(cancellationToken);
}
