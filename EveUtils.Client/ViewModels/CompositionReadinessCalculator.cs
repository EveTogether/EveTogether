using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Skills;

namespace EveUtils.Client.ViewModels;

public sealed class CompositionReadinessCalculator(
    IFitValidator validator, SkillTrainingEstimator estimator, ISdeNameResolver names)
{
    /// <summary>Per character: <c>ToFly</c> prices the fit's own requirements, <c>ToMin</c> the fit plus the entry's
    /// doctrine <paramref name="skillMinimums"/>. Flying the fit while still under the minimum is
    /// <see cref="CompositionReadinessStatus.Flies"/>.</summary>
    public CompositionReadinessEntry Evaluate(string roleName, FitReferenceInfo reference,
        IReadOnlyList<SkillMinimum> skillMinimums, IReadOnlyList<CompositionCharacterSnapshot> snapshots)
    {
        EsiFitting? fit;
        try
        {
            fit = JsonSerializer.Deserialize<EsiFitting>(reference.RawJson);
        }
        catch (JsonException)
        {
            fit = null;
        }

        List<CompositionCharacterReadiness> characters = [];
        foreach (CompositionCharacterSnapshot snapshot in snapshots)
        {
            if (!snapshot.HasSkillsScope || snapshot.Levels.Count == 0 || fit is null)
            {
                characters.Add(new CompositionCharacterReadiness(snapshot.Name,
                    CompositionReadinessStatus.Unknown, null, null, [], "Skills unavailable"));
                continue;
            }

            IReadOnlyList<SkillGap> flyGaps = validator.ValidateSkills(fit, snapshot.Levels);
            // The minimum folds into the same prerequisite closure as the fit, keeping the higher level per skill, so
            // a minimum at or below what the fit already needs adds nothing to ToMin.
            IReadOnlyList<SkillGap> minGaps = skillMinimums.Count == 0
                ? flyGaps
                : validator.SkillRequirements([fit.ShipTypeId, .. fit.Items.Select(item => item.TypeId)], skillMinimums, snapshot.Levels);
            if (minGaps.Count == 0)
            {
                characters.Add(new CompositionCharacterReadiness(snapshot.Name,
                    CompositionReadinessStatus.Ready, TimeSpan.Zero, TimeSpan.Zero, [], "No missing skills")
                {
                    HasSkillMinimums = skillMinimums.Count > 0
                });
                continue;
            }

            (TimeSpan? toFly, _) = _Price(flyGaps, snapshot);
            (TimeSpan? toMin, List<SkillGapViewModel> missing) = _Price(minGaps, snapshot);

            List<string> queued = minGaps
                .Select(gap => (Gap: gap, Level: snapshot.Queue
                    .Where(queue => queue.SkillTypeId == gap.SkillTypeId)
                    .Max(queue => (int?)queue.FinishedLevel)))
                .Where(queuedSkill => queuedSkill.Level > queuedSkill.Gap.CurrentLevel)
                .Select(queuedSkill => $"{names.TypeName(queuedSkill.Gap.SkillTypeId)} {queuedSkill.Level}")
                .ToList();
            characters.Add(new CompositionCharacterReadiness(snapshot.Name,
                flyGaps.Count == 0 ? CompositionReadinessStatus.Flies : CompositionReadinessStatus.NotYet,
                toFly, toMin, missing,
                !snapshot.HasQueueScope ? "Queue unavailable" :
                queued.Count == 0 ? "No required skills in the queue" :
                    $"Already in the queue: {string.Join(", ", queued)}")
            {
                HasSkillMinimums = skillMinimums.Count > 0
            });
        }

        return new CompositionReadinessEntry(roleName, reference.FitName,
            names.TypeName(reference.ShipTypeId), characters, hasSkillMinimums: skillMinimums.Count > 0);
    }

    /// <summary>Training time over <paramref name="gaps"/> (null without attributes) and the gaps as rows.</summary>
    private (TimeSpan? Total, List<SkillGapViewModel> Missing) _Price(
        IReadOnlyList<SkillGap> gaps, CompositionCharacterSnapshot snapshot)
    {
        TimeSpan? total = snapshot.Attributes is null ? null : TimeSpan.Zero;
        List<SkillGapViewModel> missing = [];
        foreach (SkillGap gap in gaps)
        {
            SkillTrainingEstimate? estimate = snapshot.Attributes is { } attributes
                ? estimator.Estimate(gap.SkillTypeId, gap.CurrentLevel, gap.RequiredLevel, attributes)
                : null;
            if (estimate is not null)
            {
                total += estimate.TrainingTime;
            }
            missing.Add(new SkillGapViewModel(names.TypeName(gap.SkillTypeId),
                gap.RequiredLevel, gap.CurrentLevel, estimate));
        }
        return (total, missing);
    }
}
