using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// <see cref="IFitValidator"/> over the SDE and a fit's computed stats. Skill gaps come from the index-aligned
/// requiredSkillN / requiredSkillNLevel attributes of every fitted type, diffed against the character's trained levels;
/// resource overloads come straight off the already-computed <see cref="FitStats"/> (used &gt; available). Pure compute,
/// no UI.
/// </summary>
public sealed class FitValidator(IDogmaDataAccessor data) : IFitValidator, ISingletonService
{
    public FitValidationResult Validate(EsiFitting fit, FitStats stats, IReadOnlyDictionary<int, int>? trainedSkills)
    {
        var overloads = _Overloads(stats);
        var skillGaps = trainedSkills is null ? Array.Empty<SkillGap>() : _SkillGaps(fit, trainedSkills);
        return new FitValidationResult(skillGaps, overloads);
    }

    public IReadOnlyList<SkillGap> ValidateSkills(EsiFitting fit, IReadOnlyDictionary<int, int> trainedSkills) =>
        _SkillGaps(fit, trainedSkills);

    private static IReadOnlyList<ResourceOverload> _Overloads(FitStats stats)
    {
        var overloads = new List<ResourceOverload>();
        void Check(FitResource resource, double used, double available)
        {
            if (used > available)
                overloads.Add(new ResourceOverload(resource, used, available));
        }

        Check(FitResource.Cpu, stats.CpuUsed, stats.CpuOutput);
        Check(FitResource.PowerGrid, stats.PowerUsed, stats.PowerOutput);
        Check(FitResource.Calibration, stats.CalibrationUsed, stats.CalibrationAvailable);
        Check(FitResource.DroneBay, stats.DroneBayUsed, stats.DroneBayAvailable);
        Check(FitResource.DroneBandwidth, stats.DroneBandwidthUsed, stats.DroneBandwidthAvailable);
        return overloads;
    }

    private IReadOnlyList<SkillGap> _SkillGaps(EsiFitting fit, IReadOnlyDictionary<int, int> trainedSkills)
    {
        // Collect every fitted type (ship + modules + charges + drones), then expand the required-skill tree
        // RECURSIVELY — each required skill carries its own prerequisite skills (e.g. Amarr Carrier needs Capital Ships
        // IV, which needs Jump Drive Operation V) — accumulating the highest level each skill is needed at. This matches
        // EVE's in-game "Skills Required", which lists the whole prerequisite closure, not just the directly-fitted ones.
        var required = new Dictionary<int, int>();
        var toExpand = new Queue<int>();

        void Require(int skillTypeId, int level)
        {
            if (required.TryGetValue(skillTypeId, out var current))
            {
                if (level > current)
                    required[skillTypeId] = level;   // a higher prerequisite level wins; the skill is already queued
            }
            else
            {
                required[skillTypeId] = level;
                toExpand.Enqueue(skillTypeId);        // expand this skill's own prerequisites once
            }
        }

        var fittedTypes = new HashSet<int> { fit.ShipTypeId };
        foreach (var item in fit.Items)
            fittedTypes.Add(item.TypeId);
        foreach (var typeId in fittedTypes)
            foreach (var (skillTypeId, level) in _RequiredSkills(typeId))
                Require(skillTypeId, level);

        while (toExpand.Count > 0)
            foreach (var (skillTypeId, level) in _RequiredSkills(toExpand.Dequeue()))
                Require(skillTypeId, level);

        var gaps = new List<SkillGap>();
        foreach (var (skillTypeId, requiredLevel) in required)
        {
            var currentLevel = trainedSkills.GetValueOrDefault(skillTypeId);
            if (currentLevel < requiredLevel)
                gaps.Add(new SkillGap(skillTypeId, requiredLevel, currentLevel));
        }
        return gaps;
    }

    // The (skill type, required level) pairs a type declares, from the index-aligned requiredSkillN / requiredSkillNLevel
    // base attributes. A required-skill attribute with no matching level attribute defaults to level 1.
    private IEnumerable<(int SkillTypeId, int Level)> _RequiredSkills(int typeId)
    {
        var attributes = data.GetBaseAttributes(typeId);
        for (var i = 0; i < DogmaAttributeIds.RequiredSkill.Length; i++)
        {
            var skill = attributes.FirstOrDefault(attribute => attribute.AttributeId == DogmaAttributeIds.RequiredSkill[i]);
            if (skill is null)
                continue;
            var level = attributes.FirstOrDefault(attribute => attribute.AttributeId == DogmaAttributeIds.RequiredSkillLevel[i]);
            yield return ((int)skill.Value, level is null ? 1 : (int)level.Value);
        }
    }
}
