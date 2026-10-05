using System;
using System.Globalization;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>The OPTIMISE tab's "next remap" line: a bonus remap or an elapsed cooldown means one is available now;
/// an unreported cooldown reads "unknown" rather than guessing a date (ET-354 A4).</summary>
public static class RemapAvailability
{
    public static string Describe(DateTimeOffset? accruedRemapCooldownDate, int? bonusRemaps, DateTimeOffset now)
    {
        var bonusSuffix = $" · bonus remaps {bonusRemaps ?? 0}";

        if (accruedRemapCooldownDate is not { } cooldown)
        {
            return "unknown" + bonusSuffix;
        }

        var availableNow = bonusRemaps is > 0 || cooldown <= now;
        return (availableNow
            ? "available now"
            : $"on {cooldown.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture)}") + bonusSuffix;
    }
}
