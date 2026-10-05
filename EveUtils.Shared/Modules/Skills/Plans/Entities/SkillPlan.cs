namespace EveUtils.Shared.Modules.Skills.Plans.Entities;

/// <summary>
/// A named skill plan for one character (ET-355), built from a skill, a fit, or an item — never sent anywhere: a
/// plan holds the character's own gaps (D-179), so it stays client-local like <c>ProvisionalKillmail</c>.
/// </summary>
public sealed class SkillPlan
{
    public int Id { get; set; }
    public int CharacterId { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
