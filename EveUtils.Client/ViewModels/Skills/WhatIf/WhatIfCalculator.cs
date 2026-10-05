using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Entities;
using EveUtils.Shared.Modules.Skills.Plans.Entities;

namespace EveUtils.Client.ViewModels.Skills.WhatIf;

/// <summary>
/// ET-358: the five what-if scenarios for one plan and character, pure over data the caller already holds; scenarios
/// 3-5 are each at least as fast as the one before (the remap search covers the current split and implant sets only
/// raise bonuses).
/// <list type="number">
/// <item>As the queue stands: a queued plan row lands on its own ESI <c>FinishDate</c>, every other row trains after
/// the queue ends (or "now" while paused), back to back</item>
/// <item>Plan first: every row trains now in the given order, ignoring the queue's order, except the skill training
/// right now keeps its ESI <c>FinishDate</c> progress</item>
/// <item>Plan first + remap: <see cref="AttributeRemapOptimizer.Best"/> over the plan rows, implants unchanged</item>
/// <item>+ a matched +4 implant set, on top of the remap</item>
/// <item>+ a matched +5 implant set, on top of the remap</item>
/// </list>
/// </summary>
public static class WhatIfCalculator
{
    public const int Plus4SetBonus = 4;
    public const int Plus5SetBonus = 5;

    public static IReadOnlyList<WhatIfScenario> Compute(
        IReadOnlyList<SkillPlanRow> planRows,
        IReadOnlyList<CharacterSkillQueueEntry> queue,
        IDogmaDataAccessor dogma,
        CharacterAttributeSet currentEffectiveAttributes,
        CharacterAttributeSet currentImplantBonus,
        DateTimeOffset now)
    {
        var estimator = new SkillTrainingEstimator(dogma);
        var remapRows = RemapRows(planRows, estimator);

        var asOfQueueDate = _AsQueueStands(planRows, queue, estimator, currentEffectiveAttributes, now);
        var planFirstDate = _PlanFirst(planRows, queue, estimator, currentEffectiveAttributes, now);
        var remapped = AttributeRemapOptimizer.Best(remapRows, currentImplantBonus);
        var plus4 = AttributeRemapOptimizer.Best(remapRows, ImplantSetBonus.Apply(currentImplantBonus, Plus4SetBonus));
        var plus5 = AttributeRemapOptimizer.Best(remapRows, ImplantSetBonus.Apply(currentImplantBonus, Plus5SetBonus));

        var remapDate = now + remapped.TotalTime;
        var plus4Date = now + plus4.TotalTime;
        var plus5Date = now + plus5.TotalTime;

        return
        [
            new WhatIfScenario("As the queue stands", asOfQueueDate, TimeSpan.Zero),
            new WhatIfScenario("Plan first", planFirstDate, asOfQueueDate - planFirstDate),
            new WhatIfScenario("Plan first + remap", remapDate, asOfQueueDate - remapDate, remapped.BaseAttributes),
            new WhatIfScenario("+ remap + a +4 set", plus4Date, asOfQueueDate - plus4Date, plus4.BaseAttributes),
            new WhatIfScenario("+ remap + a +5 set", plus5Date, asOfQueueDate - plus5Date, plus5.BaseAttributes)
        ];
    }

    // Scenario 2 ignores the queue's ORDER but credits the progress banked on the skill training right now (position
    // 0): its remaining time is ESI's own FinishDate, else starting immediately would look slower than "plan first" can
    // be. Every other row has not started, so the full level-to-level estimate applies.
    private static DateTimeOffset _PlanFirst(IReadOnlyList<SkillPlanRow> rows, IReadOnlyList<CharacterSkillQueueEntry> queue,
        SkillTrainingEstimator estimator, CharacterAttributeSet attributes, DateTimeOffset now)
    {
        var head = queue.Where(entry => entry.FinishDate is not null).OrderBy(entry => entry.QueuePosition).FirstOrDefault();
        var cumulative = now;
        foreach (var row in rows)
        {
            cumulative += head is { FinishDate: { } headFinish } && head.SkillTypeId == row.SkillTypeId && head.FinishedLevel == row.Level
                ? headFinish - now
                : estimator.Estimate(row.SkillTypeId, row.Level - 1, row.Level, attributes).TrainingTime;
        }

        return cumulative;
    }

    // A plan row queued right now (same skill and level, a real FinishDate) lands on that ESI date; every other row
    // queues back to back after the queue's end ("now" for a paused queue). The plan's date is the latest row date; a
    // queued row never pushes it past QueueEndsAt.
    private static DateTimeOffset _AsQueueStands(IReadOnlyList<SkillPlanRow> rows, IReadOnlyList<CharacterSkillQueueEntry> queue,
        SkillTrainingEstimator estimator, CharacterAttributeSet attributes, DateTimeOffset now)
    {
        bool isPaused = queue.Count > 0 && queue.All(entry => entry.FinishDate is null);
        DateTimeOffset queueEndsAt = queue.Select(entry => entry.FinishDate).OfType<DateTimeOffset>()
            .DefaultIfEmpty(now).Max();
        DateTimeOffset cumulative = isPaused ? now : queueEndsAt;
        DateTimeOffset latest = now;

        foreach (var row in rows)
        {
            var queued = queue.FirstOrDefault(entry =>
                entry.SkillTypeId == row.SkillTypeId && entry.FinishedLevel == row.Level && entry.FinishDate is not null);
            DateTimeOffset rowDate;
            if (queued is { FinishDate: { } queuedFinish })
            {
                rowDate = queuedFinish;
            }
            else
            {
                cumulative += estimator.Estimate(row.SkillTypeId, row.Level - 1, row.Level, attributes).TrainingTime;
                rowDate = cumulative;
            }

            if (rowDate > latest)
            {
                latest = rowDate;
            }
        }

        return latest;
    }

    // One remap row per plan row, keyed to the skill's own rank and primary/secondary training attribute — the same
    // per-skill lookup SkillTrainingEstimator.AttributesOf makes for a single estimate, generalized to a
    // level-to-level SP delta.
    public static IReadOnlyList<RemapTrainingRow> RemapRows(IReadOnlyList<SkillPlanRow> rows, SkillTrainingEstimator estimator)
    {
        var result = new List<RemapTrainingRow>();
        foreach (var row in rows)
        {
            var (rank, primaryAttributeId, secondaryAttributeId) = estimator.AttributesOf(row.SkillTypeId);
            double remainingSp = SkillPointMath.SkillPointsForLevel(rank, row.Level) - SkillPointMath.SkillPointsForLevel(rank, row.Level - 1);
            result.Add(new RemapTrainingRow(primaryAttributeId, secondaryAttributeId, remainingSp));
        }

        return result;
    }
}
