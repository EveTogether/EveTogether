using EveUtils.Shared.Modules.Map.Dtos;

namespace EveUtils.Shared.Modules.Map.Graph;

/// <summary>Breadth-first jump counts from one system to every other: the length of the shortest route, which is what
/// <see cref="RoutePlanner"/> finds for the Shortest preference with nothing avoided.</summary>
internal static class JumpCounter
{
    public static int[] From(MapGraphDto graph, int from, IReadOnlyDictionary<int, List<int>> extraNeighbours)
    {
        var jumps = new int[graph.Systems.Count];
        Array.Fill(jumps, JumpDistancesDto.Unreachable);
        var queue = new Queue<int>();
        jumps[from] = 0;
        queue.Enqueue(from);

        while (queue.TryDequeue(out int system))
        {
            foreach (int next in graph.NeighboursOf(system))
                _Visit(system, next);
            if (extraNeighbours.TryGetValue(system, out List<int>? extras))
                foreach (int next in extras)
                    _Visit(system, next);
        }
        return jumps;

        void _Visit(int system, int next)
        {
            if (jumps[next] != JumpDistancesDto.Unreachable)
                return;
            jumps[next] = jumps[system] + 1;
            queue.Enqueue(next);
        }
    }
}
