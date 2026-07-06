using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Pass 1 setup: the object graph carries ship/module group+category+state, and a skill item (level forced) is built
/// for every skill the ship or its modules require — from the all-V baseline or a character snapshot.
/// </summary>
public class DogmaFitBuilderTests
{
    [Fact]
    public void Build_SeedsShipAndModules_WithGroupCategoryAndState()
    {
        var data = new FakeDogmaDataAccessor()
            .Type(587, groupId: 25, categoryId: 6, new SdeDogmaAttribute(9, 400))
            .Type(10190, groupId: 302, categoryId: 7);

        var fit = new DogmaFitBuilder(data).Build(
            new FitInput(587, [new ModuleInput(10190, ModuleState.Online)], SkillSource.AllLevelFive));

        Assert.Equal(587, fit.Ship.TypeId);
        Assert.True(fit.Ship.IsAlwaysOn);
        Assert.Equal(6, fit.Ship.CategoryId);
        var module = Assert.Single(fit.Modules);
        Assert.Equal(302, module.GroupId);
        Assert.Equal(7, module.CategoryId);
        Assert.False(module.IsAlwaysOn);
        Assert.Equal(ModuleState.Online, module.State);
    }

    [Fact]
    public void Build_CreatesSkillItem_ForRequiredSkill_AtLevelFive()
    {
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(2488, 74, 7, new SdeDogmaAttribute(182, 3300))   // weapon requires skill 3300
            .Type(3300, 9000, 16);

        var fit = new DogmaFitBuilder(data).Build(
            new FitInput(587, [new ModuleInput(2488, ModuleState.Online)], SkillSource.AllLevelFive));

        var skill = Assert.Single(fit.Skills);
        Assert.Equal(3300, skill.TypeId);
        Assert.True(skill.IsAlwaysOn);
        Assert.True(skill.TryGet(DogmaAttributeIds.SkillLevel, out var level));
        Assert.Equal(5, level.BaseValue);
        Assert.True(level.IsForced);
    }

    [Fact]
    public void Build_InjectsDefaultDefenseMode_ForTacticalDestroyer_ReducingSignature()
    {
        const int signature = 552;
        const int modeSignaturePostDiv = 2001;
        var data = new FakeDogmaDataAccessor()
            .Attribute(signature, 0.0, stackable: false)
            .Type(34317, groupId: 1305, categoryId: 6, new SdeDogmaAttribute(signature, 100))      // Confessor (T3D hull)
            .Type(34319, groupId: 1306, categoryId: 7, new SdeDogmaAttribute(modeSignaturePostDiv, 1.5))  // Defense mode
            .TypeEffect(34319, 6014)
            .Effect(6014, 0, new ModifierInfo(ModifierFunc.ItemModifier, ModifierDomain.ShipId, 5, signature, modeSignaturePostDiv, null, null))
            .TacticalMode(34317, 34319);

        var fit = new DogmaFitBuilder(data).Build(new FitInput(34317, [], SkillSource.AllLevelFive));
        new DogmaEffectCollector(data).Collect(fit);
        var eval = new DogmaEvaluator(data);

        Assert.NotNull(fit.Mode);
        Assert.Equal(34319, fit.Mode!.TypeId);
        Assert.True(fit.Mode.IsAlwaysOn);
        Assert.Equal(100 / 1.5, eval.Resolve(fit.Ship, signature), 6);   // Defense mode PostDivides signature by 1.5
    }

    [Fact]
    public void Build_NoTacticalMode_ForNonTacticalDestroyer()
    {
        var data = new FakeDogmaDataAccessor().Type(587, groupId: 25, categoryId: 6);
        var fit = new DogmaFitBuilder(data).Build(new FitInput(587, [], SkillSource.AllLevelFive));
        Assert.Null(fit.Mode);
    }

    [Fact]
    public void Build_Structure_InjectsOnlyStructureManagementSkills()
    {
        const int structureSkill = 37798, shipSkill = 3419;
        var data = new FakeDogmaDataAccessor()
            .Type(35832, groupId: 1657, categoryId: 65)             // Astrahus (category 65 = Structure)
            .Type(587, groupId: 25, categoryId: 6)                  // a normal ship
            .Type(structureSkill, groupId: 1545, categoryId: 16)    // Structure Management skill (group 1545)
            .Type(shipSkill, groupId: 1209, categoryId: 16);        // a ship skill (Shields group)

        // The structure gets only the Structure-Management skill, never the ship-piloting skill.
        var structureSkills = new DogmaFitBuilder(data).Build(new FitInput(35832, [], SkillSource.AllLevelFive)).Skills;
        Assert.Equal([structureSkill], structureSkills.Select(s => s.TypeId));

        // A normal ship gets the full skill set.
        var shipSkills = new DogmaFitBuilder(data).Build(new FitInput(587, [], SkillSource.AllLevelFive)).Skills.Select(s => s.TypeId).ToList();
        Assert.Contains(shipSkill, shipSkills);
        Assert.Contains(structureSkill, shipSkills);
    }

    [Fact]
    public void Build_CharacterSnapshot_UsesProvidedSkillLevel()
    {
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(2488, 74, 7, new SdeDogmaAttribute(182, 3300))
            .Type(3300, 9000, 16);

        var fit = new DogmaFitBuilder(data).Build(new FitInput(587,
            [new ModuleInput(2488, ModuleState.Online)],
            SkillSource.From(new Dictionary<int, int> { [3300] = 3 })));

        var skill = Assert.Single(fit.Skills);
        Assert.True(skill.TryGet(DogmaAttributeIds.SkillLevel, out var level));
        Assert.Equal(3, level.BaseValue);
    }
}
