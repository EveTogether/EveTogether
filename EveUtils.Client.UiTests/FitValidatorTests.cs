using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Sde.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Fit validation: the skills a fit needs that the character lacks (from the requiredSkillN / level attributes)
/// and the fitting resources it overloads (used &gt; available off the computed stats). All-skills mode reports no gaps.
/// </summary>
public class FitValidatorTests
{
    private const int Ship = 587, RequiringModule = 1000, RequiredSkill = 3300;

    // A module that requires skill 3300 at level 4 (requiredSkill1 = 182, requiredSkill1Level = 277).
    private static FitValidator Validator() => new(new FakeDogmaDataAccessor()
        .Type(Ship, 25, 6)
        .Type(RequiringModule, 60, 7,
            new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[0], RequiredSkill),
            new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[0], 4)));

    private static EsiFitting FitWithModule() => new(1, "Test", "", Ship,
        [new EsiFittingItem(RequiringModule, "HiSlot0", 1)]);

    [Fact]
    public void Validate_ReportsSkillGap_WhenCharacterLacksRequiredLevel()
    {
        var result = Validator().Validate(FitWithModule(), Stats(), new Dictionary<int, int> { [RequiredSkill] = 2 });

        var gap = Assert.Single(result.SkillGaps);
        Assert.Equal(RequiredSkill, gap.SkillTypeId);
        Assert.Equal(4, gap.RequiredLevel);
        Assert.Equal(2, gap.CurrentLevel);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_NoGap_WhenSkillTrainedHighEnough()
    {
        var result = Validator().Validate(FitWithModule(), Stats(), new Dictionary<int, int> { [RequiredSkill] = 5 });

        Assert.Empty(result.SkillGaps);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_AllSkillsMode_ReportsNoSkillGaps()
    {
        // trainedSkills null = the all-level-5 planning baseline: nothing is "missing".
        var result = Validator().Validate(FitWithModule(), Stats(), trainedSkills: null);

        Assert.Empty(result.SkillGaps);
    }

    [Fact]
    public void Validate_ReportsResourceOverloads_WhenUsedExceedsAvailable()
    {
        var stats = Stats(cpuUsed: 500, cpuOutput: 400, calibrationUsed: 450, calibrationAvailable: 400);

        var result = Validator().Validate(FitWithModule(), stats, trainedSkills: null);

        Assert.Contains(result.Overloads, o => o.Resource == FitResource.Cpu && o.Used == 500 && o.Available == 400);
        Assert.Contains(result.Overloads, o => o.Resource == FitResource.Calibration);
        Assert.DoesNotContain(result.Overloads, o => o.Resource == FitResource.PowerGrid);
    }

    [Fact]
    public void ValidateSkills_ReportsOnlyGaps_WithoutNeedingStats()
    {
        // badge path: same skill diff as Validate, but no FitStats argument (no dogma stats recompute).
        var short_ = Validator().ValidateSkills(FitWithModule(), new Dictionary<int, int> { [RequiredSkill] = 2 });
        Assert.Equal(RequiredSkill, Assert.Single(short_).SkillTypeId);

        var trained = Validator().ValidateSkills(FitWithModule(), new Dictionary<int, int> { [RequiredSkill] = 4 });
        Assert.Empty(trained);
    }

    [Fact]
    public void Validate_ExpandsPrerequisiteSkills_Recursively()
    {
        const int PrerequisiteSkill = 3301;
        // The module needs RequiredSkill at I; that skill in turn requires PrerequisiteSkill at IV (its prerequisite) —
        // EVE's in-game "Skills Required" lists the whole tree, so both must surface.
        var validator = new FitValidator(new FakeDogmaDataAccessor()
            .Type(Ship, 25, 6)
            .Type(RequiringModule, 60, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[0], RequiredSkill),
                new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[0], 1))
            .Type(RequiredSkill, 0, 0,
                new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[0], PrerequisiteSkill),
                new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkillLevel[0], 4)));

        var result = validator.Validate(FitWithModule(), Stats(), new Dictionary<int, int>());   // trains nothing

        Assert.Contains(result.SkillGaps, gap => gap.SkillTypeId == RequiredSkill && gap.RequiredLevel == 1);
        Assert.Contains(result.SkillGaps, gap => gap.SkillTypeId == PrerequisiteSkill && gap.RequiredLevel == 4);
    }

    [Fact]
    public void Validate_CleanFit_IsValid()
    {
        var result = Validator().Validate(FitWithModule(), Stats(), new Dictionary<int, int> { [RequiredSkill] = 4 });

        Assert.True(result.IsValid);
        Assert.Empty(result.Overloads);
        Assert.Empty(result.SkillGaps);
    }

    private static FitStats Stats(
        double cpuUsed = 0, double cpuOutput = 100, double powerUsed = 0, double powerOutput = 100,
        double calibrationUsed = 0, double calibrationAvailable = 100,
        double droneBayUsed = 0, double droneBayAvailable = 100,
        double droneBandwidthUsed = 0, double droneBandwidthAvailable = 100) => new(
        TotalDps: 0, WeaponDps: 0, DroneDps: 0,
        CpuUsed: cpuUsed, CpuOutput: cpuOutput, PowerUsed: powerUsed, PowerOutput: powerOutput,
        DroneBayUsed: droneBayUsed, DroneBayAvailable: droneBayAvailable,
        DroneBandwidthUsed: droneBandwidthUsed, DroneBandwidthAvailable: droneBandwidthAvailable,
        CalibrationUsed: calibrationUsed, CalibrationAvailable: calibrationAvailable,
        Ehp: 0, ShieldEhp: 0, ArmorEhp: 0, StructureEhp: 0,
        ShieldResists: new ResistLayer(0, 0, 0, 0), ArmorResists: new ResistLayer(0, 0, 0, 0),
        StructureResists: new ResistLayer(0, 0, 0, 0),
        CapacitorStable: true, CapacitorStablePercent: 0, CapacitorDepletesInSeconds: 0,
        CapacitorCapacity: 0, CapacitorDelta: 0, CapacitorRecharge: 0,
        TargetingRange: 0, ScanResolution: 0, MaxLockedTargets: 0, SensorStrength: 0,
        MaxVelocity: 0, Mass: 0, Agility: 0, AlignTime: 0, WarpSpeed: 0, SignatureRadius: 0,
        ActiveDroneCount: 0, MiningYield: 0, ModuleContributions: []);
}
