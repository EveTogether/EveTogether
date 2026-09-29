using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Map.Graph;

namespace EveUtils.Shared.Modules.Map.Queries;

internal sealed class PlanRouteQueryHandler(IMapGraphCache cache, IEnumerable<IRouteEdgeSource> edgeSources)
    : IQueryHandler<PlanRouteQuery, Result<RouteDto>>
{
    public async Task<Result<RouteDto>> Handle(PlanRouteQuery query, CancellationToken cancellationToken = default)
    {
        MapGraphDto? graph = await cache.GetAsync(cancellationToken);
        if (graph is null)
            return Result<RouteDto>.Failure(MapMessages.NoMapData);
        if (!graph.TryGetIndex(query.FromSystemId, out int from))
            return Result<RouteDto>.Failure(MapMessages.UnknownSystem(query.FromSystemId));
        if (!graph.TryGetIndex(query.ToSystemId, out int to))
            return Result<RouteDto>.Failure(MapMessages.UnknownSystem(query.ToSystemId));

        IReadOnlyDictionary<int, List<int>> extras = await _ExtraNeighboursAsync(graph, cancellationToken);
        IReadOnlyList<int>? path = RoutePlanner.Plan(graph, from, to, query.Preference, query.Avoid, query.SaferPenalty, extras);
        if (path is null)
            return Result<RouteDto>.Failure(MapMessages.RouteNotFound);

        List<RouteStepDto> steps = path.Select(index =>
        {
            MapSystemDto system = graph.Systems[index];
            return new RouteStepDto(index, system.SolarSystemId, system.Name, graph.Regions[system.RegionIndex].Name,
                system.DisplaySecurity, system.Band);
        }).ToList();
        return Result<RouteDto>.Success(new RouteDto(query.Preference, steps));
    }

    private async Task<IReadOnlyDictionary<int, List<int>>> _ExtraNeighboursAsync(MapGraphDto graph, CancellationToken cancellationToken)
    {
        var extras = new Dictionary<int, List<int>>();
        foreach (IRouteEdgeSource source in edgeSources)
        {
            foreach (RouteEdgeDto edge in await source.GetEdgesAsync(cancellationToken))
            {
                if (!graph.TryGetIndex(edge.FromSystemId, out int a) || !graph.TryGetIndex(edge.ToSystemId, out int b))
                    continue;
                _Add(a, b);
                _Add(b, a);
            }
        }
        return extras;

        void _Add(int from, int to)
        {
            if (!extras.TryGetValue(from, out List<int>? list))
                extras[from] = list = [];
            list.Add(to);
        }
    }
}
