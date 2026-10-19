namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>One activity's sample with the key it counts under and the name it was flown as.</summary>
internal sealed record ActivityEarningsSample(ActivityStatsKey Key, string? SiteName, EarningsSample Sample);
