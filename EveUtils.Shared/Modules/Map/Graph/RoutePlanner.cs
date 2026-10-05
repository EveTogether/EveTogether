using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Map.Enums;

namespace EveUtils.Shared.Modules.Map.Graph;

/// <summary>
/// Dijkstra over the map, costed per system entered. Shortest costs 1 a jump; Safer makes every jump into
/// non-highsec cost <c>penalty</c> and LessSecure every jump into highsec, so the preferred space wins unless the
/// detour is longer than the penalty. An avoided band is not entered at all — except as the start or destination,
/// which the pilot chose.
/// </summary>
internal static class RoutePlanner
{
    public static IReadOnlyList<int>? Plan(
        MapGraphDto graph, int from, int to, RoutePreference preference, IReadOnlySet<SecurityBand> avoid, int penalty,
        IReadOnlyDictionary<int, List<int>> extraNeighbours)
    {
        var cost = new double[graph.Systems.Count];
        Array.Fill(cost, double.PositiveInfinity);
        var previous = new int[graph.Systems.Count];
        Array.Fill(previous, -1);
        var queue = new PriorityQueue<int, double>();
        cost[from] = 0;
        queue.Enqueue(from, 0);

        while (queue.TryDequeue(out int system, out double reached))
        {
            if (reached > cost[system])
                continue;
            if (system == to)
                break;

            foreach (int next in graph.NeighboursOf(system))
                _Relax(system, next);
            if (extraNeighbours.TryGetValue(system, out List<int>? extras))
                foreach (int next in extras)
                    _Relax(system, next);
        }

        if (double.IsPositiveInfinity(cost[to]))
            return null;
        var path = new List<int>();
        for (int system = to; system != -1; system = previous[system])
            path.Add(system);
        path.Reverse();
        return path;

        void _Relax(int system, int next)
        {
            double step = _StepCost(graph.Systems[next]);
            double candidate = cost[system] + step;
            if (candidate >= cost[next])
                return;
            cost[next] = candidate;
            previous[next] = system;
            queue.Enqueue(next, candidate);
        }

        double _StepCost(MapSystemDto entered)
        {
            if (entered.Index != from && entered.Index != to && avoid.Contains(entered.Band))
                return double.PositiveInfinity;
            bool isHigh = entered.Band == SecurityBand.High;
            return preference switch
            {
                RoutePreference.Safer => isHigh ? 1 : penalty,
                RoutePreference.LessSecure => isHigh ? penalty : 1,
                _ => 1
            };
        }
    }
}
