using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Plans.Commands;

namespace EveUtils.Client.ViewModels.Skills.Plans;

/// <summary>
/// Builds the drafts a + SKILL/FROM FIT/FROM ITEM action hands to <c>AddSkillPlanRowsCommand</c> (ET-355): the
/// prerequisite closure (<see cref="IFitValidator.SkillRequirements"/>, shared with ET-356) expanded into one row per
/// missing level, already trained-level-filtered — a plan shows only what still needs training.
/// </summary>
public static class SkillPlanRowFactory
{
    /// <summary>From a fit's ship + items: every missing skill level the fit needs, prerequisites included.</summary>
    public static SkillPlanBuildResult FromFit(
        IFitValidator validator, IReadOnlyList<int> seedTypeIds, IReadOnlyDictionary<int, int> trained, string fitName) =>
        _Build(validator, seedTypeIds, extra: null, trained, fitName);

    /// <summary>From one published SDE type (module, ship, drone, ...): the skills it needs on its own.</summary>
    public static SkillPlanBuildResult FromItem(
        IFitValidator validator, int typeId, IReadOnlyDictionary<int, int> trained, string itemName) =>
        _Build(validator, [typeId], extra: null, trained, itemName);

    /// <summary>From one explicitly chosen skill and level, with its own prerequisites.</summary>
    public static SkillPlanBuildResult FromSkill(
        IFitValidator validator, int skillTypeId, int level, IReadOnlyDictionary<int, int> trained, string skillName) =>
        _Build(validator, [], [new SkillMinimum(skillTypeId, level)], trained, skillName);

    private static SkillPlanBuildResult _Build(IFitValidator validator, IReadOnlyList<int> seedTypeIds,
        IReadOnlyList<SkillMinimum>? extra, IReadOnlyDictionary<int, int> trained, string sourceLabel)
    {
        IReadOnlyList<SkillGap> gaps = validator.SkillRequirements(seedTypeIds, extra, trained);
        var rows = new List<SkillPlanRowDraft>();
        foreach (var gap in gaps)
        {
            for (int level = gap.CurrentLevel + 1; level <= gap.RequiredLevel; level++)
            {
                rows.Add(new SkillPlanRowDraft(gap.SkillTypeId, level, sourceLabel));
            }
        }

        return rows.Count == 0
            ? new SkillPlanBuildResult(rows, $"Nothing to add for {sourceLabel} — every required skill is already trained.")
            : new SkillPlanBuildResult(rows, null);
    }
}

/// <param name="Message">Set instead of an empty <paramref name="Rows"/> silently doing nothing (ET-355 AC2) —
/// null when <paramref name="Rows"/> is non-empty.</param>
public sealed record SkillPlanBuildResult(IReadOnlyList<SkillPlanRowDraft> Rows, string? Message);
