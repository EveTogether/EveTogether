namespace EveUtils.Shared.Modules.Skills.Plans.Commands;

/// <summary>One (skill, level) to add to a plan — already prerequisite-expanded and trained-level-filtered by the
/// caller (the PLANS tab's + SKILL/FROM FIT/FROM ITEM handlers, via <c>IFitValidator.SkillRequirements</c>).</summary>
public sealed record SkillPlanRowDraft(int SkillTypeId, int Level, string? SourceLabel);
