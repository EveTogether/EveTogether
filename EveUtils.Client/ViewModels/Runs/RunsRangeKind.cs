namespace EveUtils.Client.ViewModels.Runs;

/// <summary>What the range line above the runs list is about (ET-292): the month in view, or a day or a week picked
/// in the activity strip inside it. The list itself always shows the month.</summary>
public enum RunsRangeKind
{
    Month,
    Day,
    Week
}
