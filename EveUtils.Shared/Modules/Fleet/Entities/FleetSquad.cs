namespace EveUtils.Shared.Modules.Fleet.Entities;

/// <summary>A squad within a wing (ESI: squad = id + name). Parity structure.</summary>
public sealed class FleetSquad
{
    public long Id { get; set; }
    public long WingId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>The in-game ESI squad id once the structure is pushed/mirrored; null until linked.</summary>
    public long? EsiSquadId { get; set; }
}
