namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>A wing within a fleet (ESI: wing = id + name + squads). Parity structure.</summary>
public sealed class FleetWing
{
    public long Id { get; set; }
    public long FleetId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>The in-game ESI wing id once the structure is pushed/mirrored; null until linked.</summary>
    public long? EsiWingId { get; set; }
}
