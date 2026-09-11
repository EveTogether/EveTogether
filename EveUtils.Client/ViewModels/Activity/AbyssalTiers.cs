using System.Collections.Generic;

namespace EveUtils.Client.ViewModels.Activity;

/// <summary>The seven abyssal tiers as the activity window names them, index = the T-number the filament is sold
/// under. Beside <see cref="AbyssalWeather"/> because the window's header and its ACTIVITY section both read it.</summary>
public static class AbyssalTiers
{
    public static IReadOnlyList<string> Names { get; } =
        ["Tranquil", "Calm", "Agitated", "Fierce", "Raging", "Chaotic", "Cataclysmic"];
}
