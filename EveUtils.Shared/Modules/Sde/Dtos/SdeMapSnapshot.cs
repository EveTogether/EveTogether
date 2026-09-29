namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>
/// The whole universe map from the SDE, read in one go (ET-391): every system, constellation and region plus the
/// stargate connections. Wormhole and abyssal systems are included with a null 2D position and have no jumps —
/// a consumer that draws the k-space map filters on <see cref="SdeMapSystem.X2d"/>.
/// </summary>
/// <param name="Jumps">One entry per connection with <c>FromSystemId &lt; ToSystemId</c>; mirror it for the other direction.</param>
public sealed record SdeMapSnapshot(
    IReadOnlyList<SdeMapSystem> Systems,
    IReadOnlyList<SdeMapConstellation> Constellations,
    IReadOnlyList<SdeMapRegion> Regions,
    IReadOnlyList<SdeMapJump> Jumps)
{
    public static SdeMapSnapshot Empty { get; } = new([], [], [], []);
}

/// <param name="X2d">CCP's schematic 2D position, null for systems without one (wormhole, abyssal).</param>
/// <param name="Y2d">Same, with y negated so it grows downwards like screen coordinates.</param>
public sealed record SdeMapSystem(
    int SolarSystemId, string Name, double SecurityStatus, int ConstellationId, int RegionId, double? X2d, double? Y2d);

public sealed record SdeMapConstellation(int ConstellationId, string Name, int RegionId, int? FactionId);

public sealed record SdeMapRegion(int RegionId, string Name, int? FactionId);

public sealed record SdeMapJump(int FromSystemId, int ToSystemId);
