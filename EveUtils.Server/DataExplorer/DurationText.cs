namespace EveUtils.Server.DataExplorer;

public static class DurationText
{
    /// <summary>A span at the coarsest unit that still reads at a glance: "40s", "12m", "5h", "63d".</summary>
    public static string Coarse(TimeSpan span) =>
        span < TimeSpan.FromMinutes(1) ? $"{span.TotalSeconds:0}s"
        : span < TimeSpan.FromHours(1) ? $"{span.TotalMinutes:0}m"
        : span < TimeSpan.FromDays(1) ? $"{span.TotalHours:0}h"
        : $"{span.TotalDays:0}d";

    /// <summary>A length of time flown: "42m", "1h 12m", "26h 5m" — hours never roll over into days.</summary>
    public static string HoursMinutes(TimeSpan span) =>
        span < TimeSpan.FromHours(1) ? $"{Math.Max(0, (int)span.TotalMinutes)}m" : $"{(int)span.TotalHours}h {span.Minutes}m";
}
