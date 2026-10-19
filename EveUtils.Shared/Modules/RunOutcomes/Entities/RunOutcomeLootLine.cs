using EveUtils.Shared.Modules.RunOutcomes.Enums;

namespace EveUtils.Shared.Modules.RunOutcomes.Entities;

/// <summary>One item type of a shared outcome and how many: no name, price or time, so a client values it at today's
/// price (ET-513) and the loot table counts it (ET-519).</summary>
public sealed class RunOutcomeLootLine
{
    public long Id { get; set; }
    public long RunOutcomeId { get; set; }
    public RunOutcome RunOutcome { get; set; } = null!;
    public RunOutcomeLootKind Kind { get; set; }
    public int TypeId { get; set; }
    public long Quantity { get; set; }
}
