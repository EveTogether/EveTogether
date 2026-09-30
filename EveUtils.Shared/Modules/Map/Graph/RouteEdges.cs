using EveUtils.Shared.Modules.Map.Dtos;

namespace EveUtils.Shared.Modules.Map.Graph;

/// <summary>The connections the registered <see cref="IRouteEdgeSource"/>s add to the stargates, as extra neighbours per system index.</summary>
internal static class RouteEdges
{
    public static async Task<IReadOnlyDictionary<int, List<int>>> LoadAsync(
        MapGraphDto graph, IEnumerable<IRouteEdgeSource> sources, CancellationToken cancellationToken)
    {
        var extras = new Dictionary<int, List<int>>();
        foreach (IRouteEdgeSource source in sources)
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
