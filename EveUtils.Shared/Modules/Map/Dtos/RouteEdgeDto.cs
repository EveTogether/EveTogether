namespace EveUtils.Shared.Modules.Map.Dtos;

/// <summary>A connection outside the stargate network that a route may use, both directions.</summary>
public sealed record RouteEdgeDto(int FromSystemId, int ToSystemId);
