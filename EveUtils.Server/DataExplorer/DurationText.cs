namespace EveUtils.Server.DataExplorer;

public static class DurationText
{
    /// <summary>A span at the coarsest unit that still reads at a glance: "40s", "12m", "5h", "63d".</summary>
    public static string Coarse(TimeSpan span) =>
        span < TimeSpan.FromMinutes(1) ? $"{span.TotalSeconds:0}s"
        : span < TimeSpan.FromHours(1) ? $"{span.TotalMinutes:0}m"
        : span < TimeSpan.FromDays(1) ? $"{span.TotalHours:0}h"
        : $"{span.TotalDays:0}d";
}
