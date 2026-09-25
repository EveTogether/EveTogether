using EveUtils.Shared.Modules.Skills.Plans.Entities;

namespace EveUtils.Client.ViewModels.Skills.Plans;

/// <summary>One line in the PLANS tab's table: either a trainable row or a "✈ &lt;fit&gt; flyable" milestone
/// inserted after the last row a + FROM FIT add required (ET-355 AC3).</summary>
public sealed record SkillPlanDisplayRow(
    bool IsMilestone, SkillPlanRow? Row, string SkillName, string LevelText, string TimeText, string TotalText,
    string FromText, bool QueuedNow, string? MilestoneText)
{
    public static SkillPlanDisplayRow ForRow(SkillPlanRow row, string skillName, string levelText, string timeText,
        string totalText, string fromText, bool queuedNow) =>
        new(false, row, skillName, levelText, timeText, totalText, fromText, queuedNow, null);

    public static SkillPlanDisplayRow ForMilestone(string milestoneText) =>
        new(true, null, "", "", "", "", "", false, milestoneText);
}
