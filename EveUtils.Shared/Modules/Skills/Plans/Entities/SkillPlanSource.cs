using EveUtils.Shared.Modules.Skills.Plans.Enums;

namespace EveUtils.Shared.Modules.Skills.Plans.Entities;

/// <summary>
/// One thing a plan was built from (a fit, an item, a doctrine entry, a skill), kept even when it added no row, so
/// PLANS can list "IN THIS PLAN" with its level count and say what was dropped on purpose (mockup v5, screen c).
/// </summary>
public sealed class SkillPlanSource
{
    public int Id { get; set; }
    public int PlanId { get; set; }
    public SkillPlanRowSource Source { get; set; }
    public string? SourceRef { get; set; }
    public required string Label { get; set; }

    /// <summary>The levels this source explicitly asked for that were already trained when it was added, as
    /// "skillTypeId:level" pairs joined by ','; empty when none.</summary>
    public string DroppedLevels { get; set; } = "";
}
