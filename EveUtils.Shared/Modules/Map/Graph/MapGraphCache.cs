using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Map.Graph;

/// <summary>
/// One map per SDE build for the whole app, so the map tab, the route planner and any later second view share the
/// same arrays. Keyed on the build number: after an SDE import swaps the store, the next read builds the new map.
/// The SDE read is blocking, so it always runs inside <c>Task.Run</c> (ET-298), never on the caller's thread.
/// </summary>
internal sealed class MapGraphCache(ISdeAccessor sde) : IMapGraphCache, ISingletonService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MapGraphDto? _graph;

    public async Task<MapGraphDto?> GetAsync(CancellationToken cancellationToken)
    {
        if (sde.Version?.BuildNumber is not { } build)
            return null;
        if (Volatile.Read(ref _graph) is { } current && current.BuildNumber == build)
            return current;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_graph is { } built && built.BuildNumber == build)
                return built;
            MapGraphDto? graph = await Task.Run(() => _Build(build), cancellationToken);
            Volatile.Write(ref _graph, graph);
            return graph;
        }
        finally
        {
            _gate.Release();
        }
    }

    private MapGraphDto? _Build(long build)
    {
        SdeMapSnapshot snapshot = sde.GetMapSnapshot();
        var factionNames = new Dictionary<int, string?>();
        return MapGraphBuilder.Build(snapshot, build, factionId =>
        {
            if (!factionNames.TryGetValue(factionId, out string? name))
                factionNames[factionId] = name = sde.GetFactionName(factionId);
            return name;
        });
    }
}
