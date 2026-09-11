using System.Globalization;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// "Agitated Dark" from the tier and weather stored on a run (ET-241, <c>RunParameterKey.AbyssalFilament</c>'s
/// <c>"{tier index}|{weather name}"</c>) — the tier word plus the weather, exactly how EVE itself names the filament
/// that opened the pocket. Never "Tier 4", and never "Unnamed site": an abyssal with nothing stored yet, or a row
/// whose value no longer parses (an older build, a corrupt one), reads as the type's own honest name instead.
/// </summary>
public static class AbyssalFilamentName
{
    public static string From(string? typedValue)
    {
        if (typedValue?.Split('|') is [{ } tierText, { Length: > 0 } weatherName]
            && int.TryParse(tierText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tier)
            && tier >= 0 && tier < AbyssalTiers.Names.Count)
            return $"{AbyssalTiers.Names[tier]} {weatherName}";

        return "Abyssal";
    }
}
