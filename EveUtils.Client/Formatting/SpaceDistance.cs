using System.Globalization;

namespace EveUtils.Client.Formatting;

/// <summary>A distance in space the way the overview writes it (ET-473): metres below 1 km, kilometres up to 0.1 AU,
/// astronomical units beyond.</summary>
public static class SpaceDistance
{
    public const double MetresPerAu = 149_597_870_700;

    public static string Text(double metres) => metres switch
    {
        < 1_000 => $"{metres.ToString("N0", CultureInfo.InvariantCulture)} m",
        < 0.1 * MetresPerAu => $"{(metres / 1_000).ToString("N0", CultureInfo.InvariantCulture)} km",
        _ => $"{(metres / MetresPerAu).ToString("0.00", CultureInfo.InvariantCulture)} AU"
    };
}
