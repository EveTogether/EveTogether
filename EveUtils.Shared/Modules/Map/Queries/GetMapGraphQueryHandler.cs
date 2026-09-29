using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Map.Graph;

namespace EveUtils.Shared.Modules.Map.Queries;

internal sealed class GetMapGraphQueryHandler(IMapGraphCache cache) : IQueryHandler<GetMapGraphQuery, Result<MapGraphDto>>
{
    public async Task<Result<MapGraphDto>> Handle(GetMapGraphQuery query, CancellationToken cancellationToken = default)
    {
        MapGraphDto? graph = await cache.GetAsync(cancellationToken);
        return graph is null
            ? Result<MapGraphDto>.Failure(MapMessages.NoMapData)
            : Result<MapGraphDto>.Success(graph);
    }
}
