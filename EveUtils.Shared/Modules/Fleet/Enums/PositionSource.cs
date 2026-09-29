namespace EveUtils.Shared.Modules.Fleet.Enums;

/// <summary>Where a character's position came from. Gamelog and FleetMetric are live; EsiLocation and EsiFleet are
/// read through a cached ESI endpoint and can lag a jump by several seconds.</summary>
public enum PositionSource
{
    Gamelog,
    EsiLocation,
    FleetMetric,
    EsiFleet
}
