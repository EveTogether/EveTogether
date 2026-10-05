namespace EveUtils.Shared.Modules.Skills.Plans.Enums;

/// <summary>Where a plan row's (skill, level) came from — shown as the row's FROM tag. <c>Doctrine</c> is reserved
/// for ET-386's + FROM DOCTRINE source and unused until that ticket lands.</summary>
public enum SkillPlanRowSource
{
    Skill,
    Fit,
    Item,
    Doctrine,
    Text
}
