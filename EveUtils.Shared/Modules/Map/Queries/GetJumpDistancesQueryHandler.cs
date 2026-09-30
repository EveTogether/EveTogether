using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Map.Graph;

namespace EveUtils.Shared.Modules.Map.Queries;

internal sealed class GetJumpDistancesQueryHandler(IMapGraphCache cache, IEnumerable<IRouteEdgeSource> edgeSources)
    : IQueryHandler<GetJumpDistancesQuery, Result<JumpDistancesDto>>
{
    public async Task<Result<JumpDistancesDto>> Handle(GetJumpDistancesQuery query, CancellationToken cancellationToken = default)
    {
        MapGraphDto? graph = await cache.GetAsync(cancellationToken);
        if (graph is null)
            return Result<JumpDistancesDto>.Failure(MapMessages.NoMapData);
        if (!graph.TryGetIndex(query.FromSystemId, out int from))
            return Result<JumpDistancesDto>.Failure(MapMessages.UnknownSystem(query.FromSystemId));

        IReadOnlyDictionary<int, List<int>> extras = await RouteEdges.LoadAsync(graph, edgeSources, cancellationToken);
        int[] jumps = await Task.Run(() => JumpCounter.From(graph, from, extras), cancellationToken);
        return Result<JumpDistancesDto>.Success(new JumpDistancesDto(from, jumps));
    }
}
