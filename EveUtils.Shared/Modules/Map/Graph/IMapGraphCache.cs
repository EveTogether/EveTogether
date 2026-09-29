using EveUtils.Shared.Modules.Map.Dtos;

namespace EveUtils.Shared.Modules.Map.Graph;

internal interface IMapGraphCache
{
    /// <summary>The map of the SDE build now in the store, built on a pool thread the first time it is asked for per
    /// build and shared afterwards. Null when the store has no map data (not imported yet, or an older schema).</summary>
    Task<MapGraphDto?> GetAsync(CancellationToken cancellationToken);
}
