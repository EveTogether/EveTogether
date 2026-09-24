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
    public CompositionReadinessEntry Evaluate(string roleName, FitReferenceInfo reference,
        IReadOnlyList<CompositionCharacterSnapshot> snapshots)
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
                    CompositionReadinessStatus.Unknown, null, [], "Skills unavailable"));
                continue;
            }

            IReadOnlyList<SkillGap> gaps = validator.ValidateSkills(fit, snapshot.Levels);
            if (gaps.Count == 0)
            {
                characters.Add(new CompositionCharacterReadiness(snapshot.Name,
                    CompositionReadinessStatus.Ready, TimeSpan.Zero, [], "No missing skills"));
                continue;
            }

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

            List<string> queued = gaps
                .Select(gap => (Gap: gap, Level: snapshot.Queue
                    .Where(queue => queue.SkillTypeId == gap.SkillTypeId)
                    .Max(queue => (int?)queue.FinishedLevel)))
                .Where(queuedSkill => queuedSkill.Level > queuedSkill.Gap.CurrentLevel)
                .Select(queuedSkill => $"{names.TypeName(queuedSkill.Gap.SkillTypeId)} {queuedSkill.Level}")
                .ToList();
            characters.Add(new CompositionCharacterReadiness(snapshot.Name,
                CompositionReadinessStatus.NotYet, total, missing,
                !snapshot.HasQueueScope ? "Queue unavailable" :
                queued.Count == 0 ? "No required skills in the queue" :
                    $"Already in the queue: {string.Join(", ", queued)}"));
        }

        return new CompositionReadinessEntry(roleName, reference.FitName,
            names.TypeName(reference.ShipTypeId), characters);
    }
}
