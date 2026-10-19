namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Which kind of activity an <see cref="Earnings.ActivityStatsKey"/> groups runs of (ET-513). Mining and
/// freeruns have no member: no site fixes what they pay, so they stay out of the statistic.</summary>
public enum ActivityStatsKind
{
    Site,
    Mission,
    Abyssal,
    Homefront
}
