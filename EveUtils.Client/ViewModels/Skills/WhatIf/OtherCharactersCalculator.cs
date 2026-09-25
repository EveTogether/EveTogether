using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Plans.Entities;

namespace EveUtils.Client.ViewModels.Skills.WhatIf;

/// <summary>
/// ET-358 AC3: for each of the pilot's other characters, the same prerequisite closure the PLANS tab itself uses
/// (<see cref="IFitValidator.SkillRequirements"/>) over the plan's own target levels — "fly it today" is a character
/// with no gap at all, "soonest of the rest" is the smallest training time among those with one.
/// </summary>
public static class OtherCharactersCalculator
{
    public static OtherCharactersSummary Compute(
        IReadOnlyList<CompositionCharacterSnapshot> otherCharacters,
        IReadOnlyList<SkillPlanRow> planRows,
        IFitValidator validator,
        SkillTrainingEstimator estimator,
        DateTimeOffset now)
    {
        var targets = planRows.Select(row => new SkillMinimum(row.SkillTypeId, row.Level)).ToList();

        int flyItToday = 0;
        TimeSpan? soonest = null;

        foreach (var character in otherCharacters)
        {
            if (!character.HasSkillsScope)
            {
                continue;
            }

            var gaps = validator.SkillRequirements([], targets, character.Levels);
            if (gaps.Count == 0)
            {
                flyItToday++;
                continue;
            }

            if (character.Attributes is not { } attributes)
            {
                continue; // no imported attributes — cannot estimate a training time for this character
            }

            var time = gaps.Aggregate(TimeSpan.Zero,
                (total, gap) => total + estimator.Estimate(gap.SkillTypeId, gap.CurrentLevel, gap.RequiredLevel, attributes).TrainingTime);
            if (soonest is null || time < soonest)
            {
                soonest = time;
            }
        }

        return new OtherCharactersSummary(flyItToday, soonest, soonest is null ? null : now + soonest);
    }
}
