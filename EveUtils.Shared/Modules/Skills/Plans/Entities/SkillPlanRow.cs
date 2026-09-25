using EveUtils.Shared.Modules.Skills.Plans.Enums;

namespace EveUtils.Shared.Modules.Skills.Plans.Entities;

/// <summary>
/// One (skill, level) step in a <see cref="SkillPlan"/>, at its stored position. <see cref="SourceRef"/> groups the
/// rows one + FROM FIT/ITEM/SKILL add produced (a fit's content hash, an item's type id, or null for + SKILL) — the
/// PLANS tab's "flyable" milestone is placed after the last row of the fit that seeded it.
/// </summary>
public sealed class SkillPlanRow
{
    public int Id { get; set; }
    public int PlanId { get; set; }
    public int Position { get; set; }
    public int SkillTypeId { get; set; }
    public int Level { get; set; }
    public SkillPlanRowSource Source { get; set; }
    public string? SourceRef { get; set; }

    /// <summary>Display label for the FROM column (a fit's name, an item's name) — captured at add time so a row
    /// still reads sensibly if the source fit or item is later renamed or removed.</summary>
    public string? SourceLabel { get; set; }
}
