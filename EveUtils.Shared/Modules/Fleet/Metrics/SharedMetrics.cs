namespace EveUtils.Shared.Modules.Fleet.Metrics;

/// <summary>The figures a member offers the fleet, carried by a <see cref="MetricKind.Shares"/> sample (ET-440).</summary>
[Flags]
public enum SharedMetrics
{
    None = 0,
    Location = 1,
    Combat = 2,
    Bounty = 4,
    Loot = 8,
    Mining = 16,
}
