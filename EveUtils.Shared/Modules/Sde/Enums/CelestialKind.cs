namespace EveUtils.Shared.Modules.Sde.Enums;

/// <summary>What a row of the SDE's <c>Celestial</c> table is (ET-473). Stored as its number, so values are only ever
/// added at the end.</summary>
public enum CelestialKind
{
    Star = 0,
    Planet = 1,
    Moon = 2,
    AsteroidBelt = 3,
    Stargate = 4,
    Station = 5
}
