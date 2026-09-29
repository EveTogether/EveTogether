using EveUtils.Shared.Modules.Map.Enums;

namespace EveUtils.Shared.Modules.Map.Dtos;

/// <summary>A planned route, start system first. The jump count and the security split count every system entered,
/// so the start does not count and the destination does.</summary>
public sealed record RouteDto(RoutePreference Preference, IReadOnlyList<RouteStepDto> Steps)
{
    public int Jumps => Steps.Count - 1;

    public int HighsecJumps => _Entered(SecurityBand.High);

    public int LowsecJumps => _Entered(SecurityBand.Low);

    public int NullsecJumps => _Entered(SecurityBand.Null);

    private int _Entered(SecurityBand band) => Steps.Skip(1).Count(step => step.Band == band);
}

/// <param name="SystemIndex">Index into <see cref="MapGraphDto.Systems"/>.</param>
public sealed record RouteStepDto(int SystemIndex, int SolarSystemId, string Name, string RegionName, double DisplaySecurity, SecurityBand Band);
