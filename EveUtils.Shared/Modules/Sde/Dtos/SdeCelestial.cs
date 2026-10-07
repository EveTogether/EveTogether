using EveUtils.Shared.Modules.Sde.Enums;

namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>
/// A sun, planet, moon, belt, stargate or NPC station of one solar system, with its position in metres in the system's
/// own frame — the sun sits at the origin (ET-473). The name is built the way the game shows it ("Arnher VIII - Moon
/// 1", "Stargate (Evati)"); a stargate also carries the system it leads to and that system's security.
/// </summary>
public sealed record SdeCelestial(
    int ItemId, CelestialKind Kind, string Name, double X, double Y, double Z,
    int? DestinationSystemId = null, string? DestinationName = null, double? DestinationSecurity = null);
