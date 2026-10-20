namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One counted activity's gross yield with the weight it carries in the statistic (ET-517): what a
/// distribution is drawn from, without anybody's name on it.</summary>
public sealed class EarningsYieldDto
{
    public required decimal YieldIsk { get; init; }
    public required double Weight { get; init; }

    /// <summary>The own activity this yield is (ET-520), so a run can find itself in the distribution; null for a
    /// server's outcome, which never carries one.</summary>
    public string? ActivityRef { get; init; }
}
