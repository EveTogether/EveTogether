namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>One real activity behind the figures — the worst or the best — without anybody's name on it (ET-513).</summary>
public sealed class EarningsRunDto
{
    public required DateTime CompletedAtUtc { get; init; }
    public required decimal YieldIsk { get; init; }
    public required int? DurationSeconds { get; init; }
    public required int FleetSize { get; init; }
}
