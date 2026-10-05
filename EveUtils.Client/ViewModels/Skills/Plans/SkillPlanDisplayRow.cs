using EveUtils.Shared.Modules.Skills.Plans.Entities;

namespace EveUtils.Client.ViewModels.Skills.Plans;

/// <summary>One line in the PLANS tab's table (mockup v5 screen c): a trainable row — number, pips, skill and level,
/// "QUEUE #n" when it already waits in the ESI queue, group, rank and attributes, time, running total and the FROM chip —
/// or a milestone ("✈ Claymore flyable", "◆ … minimum met") after the row that completes it.</summary>
public sealed record SkillPlanDisplayRow(
    bool IsMilestone, SkillPlanRow? Row, string SkillName, string LevelText, string TimeText, string TotalText,
    string FromText, bool QueuedNow, string? MilestoneText)
{
    public int Number { get; init; }
    public int CurrentLevel { get; init; }
    public int Level => Row?.Level ?? 0;
    public string GroupName { get; init; } = "";
    public string RankAttributesText { get; init; } = "";
    public string QueueChipText { get; init; } = "";
    public bool HasQueueChip => QueueChipText.Length > 0;
    public string MilestoneDetail { get; init; } = "";
    public bool IsFit => FromText == "FIT";
    public bool IsMin => FromText == "MIN";
    public bool IsItem => FromText == "ITEM";

    public static SkillPlanDisplayRow ForRow(SkillPlanRow row, string skillName, string levelText, string timeText,
        string totalText, string fromText, bool queuedNow) =>
        new(false, row, skillName, levelText, timeText, totalText, fromText, queuedNow, null);

    public static SkillPlanDisplayRow ForMilestone(string milestoneText, string detail = "") =>
        new(true, null, "", "", "", "", "", false, milestoneText) { MilestoneDetail = detail };
}
