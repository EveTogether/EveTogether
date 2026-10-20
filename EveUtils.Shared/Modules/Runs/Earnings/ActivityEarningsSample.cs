namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>One activity's sample with the key it counts under and the name it was flown as, the reference the
/// activity goes by (<c>GroupCode ?? Id</c>) and the pilot whose run opened it.</summary>
internal sealed record ActivityEarningsSample(ActivityStatsKey Key, string? SiteName, EarningsSample Sample,
    string ActivityRef, long CharacterId);
