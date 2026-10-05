namespace EveUtils.Client.ViewModels.Skills.Plans;

/// <summary>One "IN THIS PLAN" line (mockup v5 screen c): the source's FROM chip, its name, and how many of the plan's
/// levels it brought in (0 when it was already trained).</summary>
public sealed record PlanSourceRow(string Kind, string Label, string LevelsText)
{
    public bool IsFit => Kind == "FIT";
    public bool IsMin => Kind == "MIN";
    public bool IsItem => Kind == "ITEM";
}

/// <summary>One REQUIRED line of the "Add from an item" pane: a skill level the item needs, and whether it is trained.</summary>
public sealed record PlanItemRequirementRow(string SkillText, string StateText, bool IsTrained);
