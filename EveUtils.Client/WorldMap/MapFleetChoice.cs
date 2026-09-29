namespace EveUtils.Client.WorldMap;

/// <summary>A fleet the map can follow.</summary>
/// <param name="ServerAddress">The server the fleet lives on; null for a client-only fleet.</param>
public sealed record MapFleetChoice(long FleetId, string Name, string? ServerAddress);
