using EveUtils.Shared.Modules.Map.Dtos;

namespace EveUtils.Shared.Modules.Map;

/// <summary>
/// Connections the SDE's stargates do not cover — reserved for jump bridges and Thera/Turnur wormholes (ET-390).
/// The route planner already takes every registered source into account; none is registered yet.
/// </summary>
public interface IRouteEdgeSource
{
    /// <summary>The connections known right now. An edge naming a system the map does not have is ignored.</summary>
    Task<IReadOnlyList<RouteEdgeDto>> GetEdgesAsync(CancellationToken cancellationToken);
}
