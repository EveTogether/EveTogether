using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills.Plans.Entities;
using EveUtils.Shared.Modules.Skills.Plans.Enums;

namespace EveUtils.Client.ViewModels.Skills.Plans;

/// <summary>
/// Orders a plan's rows for display (ET-355): whichever <see cref="SkillPlanOrderMode"/> the pilot picked, a
/// prerequisite's rows always come before the rows of the skill that needs it (AC8) — the order choice only decides
/// which of several currently-trainable skills goes next, never whether one can jump ahead of its own prerequisite.
/// Grouped by skill (a skill's own levels always stay a contiguous, ascending block) since a skill is trained
/// level-by-level in-game regardless of what else is queued around it.
/// </summary>
public static class SkillPlanOrdering
{
    /// <summary>Topologically orders <paramref name="rows"/> by skill, respecting every skill-to-skill prerequisite
    /// among the plan's own skills. <paramref name="timePerSkill"/> (skill type id → total training time for that
    /// skill's rows in this plan) drives <see cref="SkillPlanOrderMode.ShortestFirst"/>; omit it for the other modes.</summary>
    public static IReadOnlyList<SkillPlanRow> Order(IReadOnlyList<SkillPlanRow> rows, SkillPlanOrderMode mode,
        IDogmaDataAccessor dogma, IReadOnlyDictionary<int, TimeSpan>? timePerSkill = null)
    {
        var discoveryOrder = rows.Select(row => row.SkillTypeId).Distinct().ToList();
        var bySkill = discoveryOrder.ToDictionary(skillTypeId => skillTypeId,
            skillTypeId => (IReadOnlyList<SkillPlanRow>)rows.Where(r => r.SkillTypeId == skillTypeId).OrderBy(r => r.Level).ToList());

        var dependsOn = discoveryOrder.ToDictionary(skillTypeId => skillTypeId, skillTypeId =>
            _RequiredSkillsOf(skillTypeId, dogma).Select(req => req.SkillTypeId).Where(bySkill.ContainsKey).ToHashSet());

        double Priority(int skillTypeId) => mode switch
        {
            SkillPlanOrderMode.ShortestFirst => timePerSkill is { } byTime && byTime.TryGetValue(skillTypeId, out var time)
                ? time.TotalSeconds : 0,
            SkillPlanOrderMode.ByAttribute => _PrimaryAttributeOf(skillTypeId, dogma),
            _ => discoveryOrder.IndexOf(skillTypeId)
        };

        var remaining = new List<int>(discoveryOrder);
        var scheduled = new List<int>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(skillTypeId => dependsOn[skillTypeId].All(scheduled.Contains))
                .OrderBy(Priority).ThenBy(discoveryOrder.IndexOf).ToList();
            int next = ready.Count > 0 ? ready[0] : remaining[0]; // a dependency cycle never happens for real skills; fall back rather than hang
            scheduled.Add(next);
            remaining.Remove(next);
        }

        return scheduled.SelectMany(skillTypeId => bySkill[skillTypeId]).ToList();
    }

    /// <summary>The index in <paramref name="ordered"/> of the last row belonging to <paramref name="sourceRef"/>
    /// (a + FROM FIT add's fit identity) — where the "✈ flyable" milestone goes (AC3). Null when no row carries it.</summary>
    public static int? FlyableMilestoneIndex(IReadOnlyList<SkillPlanRow> ordered, string sourceRef) =>
        _LastMatchIndex(ordered, row => row.Source == SkillPlanRowSource.Fit && row.SourceRef == sourceRef);

    /// <summary>The index in <paramref name="ordered"/> of the last row belonging to a + FROM DOCTRINE add's
    /// <paramref name="sourceRef"/> (the composition entry) — where the "◆ doctrine minimum met" milestone goes
    /// (ET-386 AC3), always at or after <see cref="DoctrineFlyableMilestoneIndex"/>. Null when no row carries it.</summary>
    public static int? DoctrineMinimumMilestoneIndex(IReadOnlyList<SkillPlanRow> ordered, string sourceRef) =>
        _LastMatchIndex(ordered, row => row.Source == SkillPlanRowSource.Doctrine && row.SourceRef == sourceRef);

    /// <summary>The index in <paramref name="ordered"/> of the last row belonging to a + FROM DOCTRINE add's
    /// <paramref name="sourceRef"/> that the fit itself required — not only its skill minimums — where the
    /// "✈ flyable" milestone goes for that add (ET-386 AC3). Null when no row carries it.</summary>
    public static int? DoctrineFlyableMilestoneIndex(IReadOnlyList<SkillPlanRow> ordered, string sourceRef,
        IReadOnlySet<(int SkillTypeId, int Level)> fitRequiredLevels) =>
        _LastMatchIndex(ordered, row => row.Source == SkillPlanRowSource.Doctrine && row.SourceRef == sourceRef
            && fitRequiredLevels.Contains((row.SkillTypeId, row.Level)));

    private static int? _LastMatchIndex(IReadOnlyList<SkillPlanRow> ordered, Func<SkillPlanRow, bool> predicate)
    {
        int index = -1;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (predicate(ordered[i]))
            {
                index = i;
            }
        }
        return index < 0 ? null : index;
    }

    private static IEnumerable<(int SkillTypeId, int Level)> _RequiredSkillsOf(int typeId, IDogmaDataAccessor dogma)
    {
        var attributes = dogma.GetBaseAttributes(typeId);
        for (int i = 0; i < DogmaAttributeIds.RequiredSkill.Length; i++)
        {
            var skill = attributes.FirstOrDefault(a => a.AttributeId == DogmaAttributeIds.RequiredSkill[i]);
            if (skill is null)
            {
                continue;
            }

            var level = attributes.FirstOrDefault(a => a.AttributeId == DogmaAttributeIds.RequiredSkillLevel[i]);
            yield return ((int)skill.Value, level is null ? 1 : (int)level.Value);
        }
    }

    private static double _PrimaryAttributeOf(int skillTypeId, IDogmaDataAccessor dogma) =>
        dogma.GetBaseAttributes(skillTypeId).FirstOrDefault(a => a.AttributeId == DogmaAttributeIds.SkillPrimaryAttribute)?.Value ?? 0;
}
