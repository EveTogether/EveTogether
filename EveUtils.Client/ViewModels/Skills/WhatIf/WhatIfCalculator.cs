using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Entities;
using EveUtils.Shared.Modules.Skills.Plans.Entities;

namespace EveUtils.Client.ViewModels.Skills.WhatIf;

/// <summary>
/// ET-358: the five what-if scenarios for one plan and character, pure over data the caller already holds — nothing
/// here reads a repository or writes anything.
///
/// <list type="number">
/// <item>As the queue stands: a plan row already in the queue lands on its own ESI <c>FinishDate</c>; every other
/// row trains after the queue ends (or "now" while paused), back to back, at the character's current attributes.</item>
/// <item>Plan first: every row trains now, in the given order, ignoring the queue's order — except that the one
/// skill actively training right now keeps the progress its own ESI <c>FinishDate</c> already reflects.</item>
/// <item>Plan first + remap: <see cref="AttributeRemapOptimizer.Best"/> over the plan rows, implants unchanged.</item>
/// <item>+ a matched +4 implant set, on top of the remap.</item>
/// <item>+ a matched +5 implant set, on top of the remap.</item>
/// </list>
///
/// Scenarios 3-5 are each at least as fast as the one before: <see cref="AttributeRemapOptimizer.Best"/> searches
/// every valid base split, so it can never be slower than the character's own current split (scenario 2's baseline),
/// and <see cref="ImplantSetBonus.Apply"/> only ever raises a bonus (<c>Math.Max</c>), never lowers it.
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
        var remapRows = _RemapRows(planRows, estimator);

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
            new WhatIfScenario("+ set +4 implants", plus4Date, asOfQueueDate - plus4Date, plus4.BaseAttributes),
            new WhatIfScenario("+ set +5 implants", plus5Date, asOfQueueDate - plus5Date, plus5.BaseAttributes)
        ];
    }

    // Scenario 2 ignores the queue's ORDER, but not the progress already banked on whichever skill is actively
    // training right now (queue position 0): that one row's true remaining time is exactly what ESI's own
    // FinishDate already says, not a fresh from-scratch estimate — crediting nothing here would make starting
    // immediately look slower than waiting for the queue to run its course, which "plan first" can never be.
    // Every other row, queued later or not queued at all, has not started, so the full level-to-level estimate applies.
    private static DateTimeOffset _PlanFirst(IReadOnlyList<SkillPlanRow> rows, IReadOnlyList<CharacterSkillQueueEntry> queue,
        SkillTrainingEstimator estimator, CharacterAttributeSet attributes, DateTimeOffset now)
    {
        var head = queue.Where(entry => entry.FinishDate is not null).OrderBy(entry => entry.QueuePosition).FirstOrDefault();
        var cumulative = now;
        foreach (var row in rows)
        {
            cumulative += head is not null && head.SkillTypeId == row.SkillTypeId && head.FinishedLevel == row.Level
                ? head.FinishDate!.Value - now
                : estimator.Estimate(row.SkillTypeId, row.Level - 1, row.Level, attributes).TrainingTime;
        }

        return cumulative;
    }

    // A plan row queued right now (same skill, same target level, a real FinishDate) lands on that ESI date — it is
    // already being trained. Everything else queues up after the current queue ends (or "now" for a paused queue,
    // which reports no FinishDate at all), back to back in the given order. The plan's own completion date is the
    // latest of every row's date: a queued row can never push it out (it always falls within the queue, at or before
    // QueueEndsAt), but it can leave it exactly at the queue's own end when every row is already queued.
    private static DateTimeOffset _AsQueueStands(IReadOnlyList<SkillPlanRow> rows, IReadOnlyList<CharacterSkillQueueEntry> queue,
        SkillTrainingEstimator estimator, CharacterAttributeSet attributes, DateTimeOffset now)
    {
        bool isPaused = queue.Count > 0 && queue.All(entry => entry.FinishDate is null);
        DateTimeOffset queueEndsAt = queue.Where(entry => entry.FinishDate is not null)
            .Select(entry => entry.FinishDate!.Value).DefaultIfEmpty(now).Max();
        DateTimeOffset cumulative = isPaused ? now : queueEndsAt;
        DateTimeOffset latest = now;

        foreach (var row in rows)
        {
            var queued = queue.FirstOrDefault(entry =>
                entry.SkillTypeId == row.SkillTypeId && entry.FinishedLevel == row.Level && entry.FinishDate is not null);
            DateTimeOffset rowDate;
            if (queued is not null)
            {
                rowDate = queued.FinishDate!.Value;
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
    private static IReadOnlyList<RemapTrainingRow> _RemapRows(IReadOnlyList<SkillPlanRow> rows, SkillTrainingEstimator estimator)
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
