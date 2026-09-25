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

    /// <summary>From a doctrine entry (ET-386): the fit's own gaps plus its skill minimums' gaps, prerequisites
    /// included — the fit-required (skill, level) pairs alone are <see cref="RequiredLevelPairs"/>, used to place the
    /// ✈ flyable milestone before the later ◆ doctrine minimum met milestone.</summary>
    public static SkillPlanBuildResult FromDoctrine(IFitValidator validator, IReadOnlyList<int> seedTypeIds,
        IReadOnlyList<SkillMinimum> skillMinimums, IReadOnlyDictionary<int, int> trained, string sourceLabel) =>
        _Build(validator, seedTypeIds, skillMinimums, trained, sourceLabel);

    /// <summary>Every (skillTypeId, level) pair <paramref name="seedTypeIds"/> requires on its own, with no extra
    /// minimums folded in — the fit-required half of a doctrine add's two milestones (ET-386).</summary>
    public static IReadOnlySet<(int SkillTypeId, int Level)> RequiredLevelPairs(
        IFitValidator validator, IReadOnlyList<int> seedTypeIds, IReadOnlyDictionary<int, int> trained) =>
        _ExpandGaps(validator.SkillRequirements(seedTypeIds, extra: null, trained)).ToHashSet();

    private static SkillPlanBuildResult _Build(IFitValidator validator, IReadOnlyList<int> seedTypeIds,
        IReadOnlyList<SkillMinimum>? extra, IReadOnlyDictionary<int, int> trained, string sourceLabel)
    {
        IReadOnlyList<SkillGap> gaps = validator.SkillRequirements(seedTypeIds, extra, trained);
        var rows = _ExpandGaps(gaps).Select(pair => new SkillPlanRowDraft(pair.SkillTypeId, pair.Level, sourceLabel)).ToList();

        return rows.Count == 0
            ? new SkillPlanBuildResult(rows, $"Nothing to add for {sourceLabel} — every required skill is already trained.")
            : new SkillPlanBuildResult(rows, null);
    }

    private static IEnumerable<(int SkillTypeId, int Level)> _ExpandGaps(IReadOnlyList<SkillGap> gaps) =>
        gaps.SelectMany(gap => Enumerable.Range(gap.CurrentLevel + 1, gap.RequiredLevel - gap.CurrentLevel), (gap, level) => (gap.SkillTypeId, level));
}

/// <param name="Message">Set instead of an empty <paramref name="Rows"/> silently doing nothing (ET-355 AC2) —
/// null when <paramref name="Rows"/> is non-empty.</param>
public sealed record SkillPlanBuildResult(IReadOnlyList<SkillPlanRowDraft> Rows, string? Message);
