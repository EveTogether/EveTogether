using EveUtils.Shared.Modules.Runs.Earnings;

namespace EveUtils.Shared.Modules.RunOutcomes.Dtos;

/// <summary>The shared outcomes of one activity in a window, without their loot lines (those come lazily, through
/// <c>GetRunOutcomeLootQuery</c>).</summary>
public sealed class ActivityOutcomesDto
{
    public required ActivityStatsKey Key { get; init; }

    /// <summary>How many pilots the counted outcomes came from: the one figure about the sharers a client gets.</summary>
    public required int PilotCount { get; init; }

    public required IReadOnlyList<RunOutcomeDto> Outcomes { get; init; }
}
