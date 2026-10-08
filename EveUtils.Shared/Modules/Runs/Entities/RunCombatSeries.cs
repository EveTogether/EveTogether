using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

/// <summary>
/// One series of a <see cref="RunCombatTimeline"/>, stored only when it holds anything: the per-second sums as
/// deflated int32 (<see cref="Telemetry.RunCombatTelemetry"/>), and their total so a list never has to open them.
/// </summary>
public sealed class RunCombatSeries
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public CombatSeriesKind Kind { get; set; }
    public long Total { get; set; }
    public byte[] Samples { get; set; } = [];
}
