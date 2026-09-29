using EveUtils.Shared.Modules.Map.Enums;

namespace EveUtils.Shared.Modules.Map.Dtos;

/// <summary>One k-space system on the map.</summary>
/// <param name="Index">Position in <see cref="MapGraphDto.Systems"/>; every other index in the graph points here.</param>
/// <param name="DisplaySecurity">Security rounded to one decimal the way the game shows it.</param>
/// <param name="X">Map position in world units, 0 at the far west; see <see cref="MapGraphDto.Width"/>.</param>
/// <param name="Y">Same, 0 at the far north, growing downwards.</param>
public sealed record MapSystemDto(
    int Index,
    int SolarSystemId,
    string Name,
    double Security,
    double DisplaySecurity,
    SecurityBand Band,
    double X,
    double Y,
    int ConstellationIndex,
    int RegionIndex);
