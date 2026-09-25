using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Skills;

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

    public IReadOnlyList<SkillGap> SkillRequirements(
        IEnumerable<int> seedTypeIds, IEnumerable<SkillMinimum>? extra, IReadOnlyDictionary<int, int> trained)
    {
        // Same recursive prerequisite-closure walk as _SkillGaps below, generalized: the seed types contribute their
        // own requiredSkillN attributes exactly as a fit's ship + items do, and extra minimums fold in the same way
        // a fitted type's required skill would — including their own expansion, so a candidate skill's prerequisites
        // are priced too.
        var required = new Dictionary<int, int>();
        var toExpand = new Queue<int>();

        void Require(int skillTypeId, int level)
        {
            if (required.TryGetValue(skillTypeId, out var current))
            {
                if (level > current)
                    required[skillTypeId] = level;
            }
            else
            {
                required[skillTypeId] = level;
                toExpand.Enqueue(skillTypeId);
            }
        }

        foreach (var typeId in seedTypeIds)
            foreach (var (skillTypeId, level) in _RequiredSkills(typeId))
                Require(skillTypeId, level);

        foreach (var minimum in extra ?? [])
            Require(minimum.SkillTypeId, minimum.Level);

        while (toExpand.Count > 0)
            foreach (var (skillTypeId, level) in _RequiredSkills(toExpand.Dequeue()))
                Require(skillTypeId, level);

        var gaps = new List<SkillGap>();
        foreach (var (skillTypeId, requiredLevel) in required)
        {
            var currentLevel = trained.GetValueOrDefault(skillTypeId);
            if (currentLevel < requiredLevel)
                gaps.Add(new SkillGap(skillTypeId, requiredLevel, currentLevel));
        }
        return gaps;
    }

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

    // Every fitted type (ship + modules + charges + drones) seeds SkillRequirements' RECURSIVE required-skill walk —
    // each required skill carries its own prerequisite skills (e.g. Amarr Carrier needs Capital Ships IV, which needs
    // Jump Drive Operation V) — accumulating the highest level each skill is needed at. This matches EVE's in-game
    // "Skills Required", which lists the whole prerequisite closure, not just the directly-fitted ones.
    private IReadOnlyList<SkillGap> _SkillGaps(EsiFitting fit, IReadOnlyDictionary<int, int> trainedSkills)
    {
        var fittedTypes = new HashSet<int> { fit.ShipTypeId };
        foreach (var item in fit.Items)
            fittedTypes.Add(item.TypeId);
        return SkillRequirements(fittedTypes, null, trainedSkills);
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
