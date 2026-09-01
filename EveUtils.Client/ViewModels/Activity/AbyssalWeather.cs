using System.Collections.Generic;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>
/// One of the five abyssal weathers as the activity window names it. Display copy only — the dogma side (which
/// attribute the bonus and the resist penalty land on) is <c>AbyssalBeacons</c>, which carries no wording and
/// deliberately no Dark Matter Field, so neither side can be derived from the other.
/// </summary>
public sealed record AbyssalWeather(string Name, string EnvironmentName, string Bonus, string PenaltyTarget)
{
    /// <summary>The five weathers, in the order the picker offers them.</summary>
    public static IReadOnlyList<AbyssalWeather> All { get; } =
    [
        new("Dark", "Dark Matter Field", "+50% max velocity", "turret optimal + falloff"),
        new("Electrical", "Electrical Storm", "-50% capacitor recharge time", "EM resistance"),
        new("Exotic", "Exotic Particle Storm", "+50% scan resolution", "kinetic resistance"),
        new("Firestorm", "Plasma Firestorm", "+50% armor", "thermal resistance"),
        new("Gamma", "Gamma-Ray Afterglow", "+50% shield", "explosive resistance")
    ];
}
