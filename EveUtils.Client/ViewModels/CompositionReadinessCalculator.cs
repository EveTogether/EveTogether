using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using EveUtils.Client.Fleet;
using EveUtils.Client.Skills;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.ViewModels.Skills.WhatIf;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills;
using EveUtils.Shared.Modules.Skills.Plans.Entities;

namespace EveUtils.Client.ViewModels;

public sealed class CompositionReadinessCalculator(
    IFitValidator validator, SkillTrainingEstimator estimator, ISdeNameResolver names, IDogmaDataAccessor? dogma = null)
{
    private static readonly CharacterAttributeSet NoImplantBonus = new(0, 0, 0, 0, 0);

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

        // The fit's own (skill, level) closure: "n skills required by the fit" and the editor's "fit needs" hint.
        IReadOnlyDictionary<int, int> fitLevels = fit is null
            ? new Dictionary<int, int>()
            : validator.ValidateSkills(fit, new Dictionary<int, int>()).ToDictionary(gap => gap.SkillTypeId, gap => gap.RequiredLevel);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<CompositionCharacterReadiness> characters = [];
        foreach (CompositionCharacterSnapshot snapshot in snapshots)
        {
            if (!snapshot.HasSkillsScope || snapshot.Levels.Count == 0 || fit is null)
            {
                characters.Add(new CompositionCharacterReadiness(snapshot.Name,
                    CompositionReadinessStatus.Unknown, null, null, [], "Skills unavailable")
                {
                    CharacterId = snapshot.CharacterId
                });
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
                    HasSkillMinimums = skillMinimums.Count > 0,
                    CharacterId = snapshot.CharacterId
                });
                continue;
            }

            (TimeSpan? toFly, _) = _Price(flyGaps, snapshot);
            (TimeSpan? toMin, List<SkillGapViewModel> missing) = _Price(minGaps, snapshot);
            List<CompositionMissingLevel> missingLevels = _MissingLevels(minGaps, snapshot);

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
                HasSkillMinimums = skillMinimums.Count > 0,
                CharacterId = snapshot.CharacterId,
                FlyLevels = _LevelCount(flyGaps),
                MinLevels = _LevelCount(minGaps),
                MissingLevels = missingLevels,
                Scenarios = _Scenarios(missingLevels, snapshot, now)
            });
        }

        return new CompositionReadinessEntry(roleName, reference.FitName,
            names.TypeName(reference.ShipTypeId), characters, hasSkillMinimums: skillMinimums.Count > 0)
        {
            FitSkillLevels = fitLevels,
            FitRequiresText = FitRequirementsText.Format(fitLevels, names.TypeName),
            MinimumChips = [.. skillMinimums.Select(minimum => $"{names.TypeName(minimum.SkillTypeId)} {RomanLevel.Text(minimum.Level)}")]
        };
    }

    private static int _LevelCount(IReadOnlyList<SkillGap> gaps) => gaps.Sum(gap => gap.RequiredLevel - gap.CurrentLevel);

    // One row per missing level, priced level by level, with its place in the queue when it is already queued.
    private List<CompositionMissingLevel> _MissingLevels(IReadOnlyList<SkillGap> gaps, CompositionCharacterSnapshot snapshot) =>
    [
        .. gaps.SelectMany(gap => Enumerable.Range(gap.CurrentLevel + 1, gap.RequiredLevel - gap.CurrentLevel), (gap, level) =>
            new CompositionMissingLevel(gap.SkillTypeId, level, names.TypeName(gap.SkillTypeId),
                snapshot.Attributes is { } attributes
                    ? estimator.Estimate(gap.SkillTypeId, level - 1, level, attributes).TrainingTime
                    : null,
                snapshot.Queue.FirstOrDefault(queued => queued.SkillTypeId == gap.SkillTypeId && queued.FinishedLevel == level)
                    is { } entry ? entry.QueuePosition + 1 : null))
    ];

    // The missing levels as an unsaved plan, through the what-if's own scenarios: "as the queue stands" and "plan first".
    private Lazy<IReadOnlyList<WhatIfScenario>>? _Scenarios(
        List<CompositionMissingLevel> missingLevels, CompositionCharacterSnapshot snapshot, DateTimeOffset now)
    {
        if (dogma is null || snapshot.Attributes is not { } attributes || missingLevels.Count == 0)
        {
            return null;
        }
        List<SkillPlanRow> rows = [.. missingLevels.Select((level, index) =>
            new SkillPlanRow { Position = index, SkillTypeId = level.SkillTypeId, Level = level.Level })];
        return new Lazy<IReadOnlyList<WhatIfScenario>>(() =>
            WhatIfCalculator.Compute(rows, snapshot.Queue, dogma, attributes, NoImplantBonus, now));
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
