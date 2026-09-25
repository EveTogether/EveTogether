using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Plans.Entities;

namespace EveUtils.Client.ViewModels.Skills.Plans;

/// <summary>
/// Per-row and total training time for a plan (ET-355 AC4) via <see cref="SkillTrainingEstimator"/> — each row is one
/// (skill, level) step, so its own time is the estimate from <c>Level - 1</c> to <c>Level</c>. The total is a plain
/// sum over the row set, so it comes out the same for every <see cref="SkillPlanOrderMode"/>: order never changes
/// what has to be trained, only the sequence it happens in.
/// </summary>
public static class SkillPlanTiming
{
    public static TimeSpan RowTime(SkillTrainingEstimator estimator, SkillPlanRow row, CharacterAttributeSet attributes) =>
        estimator.Estimate(row.SkillTypeId, row.Level - 1, row.Level, attributes).TrainingTime;

    public static TimeSpan TotalTime(SkillTrainingEstimator estimator, IReadOnlyList<SkillPlanRow> rows, CharacterAttributeSet attributes) =>
        rows.Aggregate(TimeSpan.Zero, (total, row) => total + RowTime(estimator, row, attributes));

    /// <summary>Per-skill total time, for <see cref="SkillPlanOrderMode.ShortestFirst"/>'s ordering priority.</summary>
    public static IReadOnlyDictionary<int, TimeSpan> TimePerSkill(
        SkillTrainingEstimator estimator, IReadOnlyList<SkillPlanRow> rows, CharacterAttributeSet attributes) =>
        rows.GroupBy(row => row.SkillTypeId)
            .ToDictionary(group => group.Key, group => group.Aggregate(TimeSpan.Zero, (total, row) => total + RowTime(estimator, row, attributes)));
}
