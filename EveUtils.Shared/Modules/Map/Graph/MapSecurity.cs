using EveUtils.Shared.Modules.Map.Enums;

namespace EveUtils.Shared.Modules.Map.Graph;

/// <summary>Security status as the game shows it and the band it falls in.</summary>
internal static class MapSecurity
{
    /// <summary>Rounded half up to one decimal, except that anything above 0.0 and below 0.05 shows as 0.1 — the game
    /// never shows a lowsec system as 0.0. Half up is what makes a 0.45 system highsec.</summary>
    public static double Display(double security)
    {
        if (security is > 0 and < 0.05)
            return 0.1;
        return Math.Round(security * 10, MidpointRounding.AwayFromZero) / 10;
    }

    public static SecurityBand Band(double displaySecurity) => displaySecurity switch
    {
        >= 0.5 => SecurityBand.High,
        > 0 => SecurityBand.Low,
        _ => SecurityBand.Null
    };
}
