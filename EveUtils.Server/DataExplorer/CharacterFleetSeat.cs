using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Server.DataExplorer;

/// <summary>A character's place on one fleet's roster.</summary>
public sealed class CharacterFleetSeat
{
    public required FleetListItem Fleet { get; init; }
    public required FleetRole Role { get; init; }
    public int? ShipTypeId { get; init; }
}
