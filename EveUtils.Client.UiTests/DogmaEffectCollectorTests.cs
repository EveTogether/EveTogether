using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Pass 2 routing: each modifier lands on the right target(s) per Func/Domain, with the correct operator and the
/// pre-computed stacking-penalty flag. Covers the skip paths (EffectStopper, operation 9, OtherID without a charge,
/// offline modules) the design calls out (V-3/V-4 + state-gate).
/// </summary>
public class DogmaEffectCollectorTests
{
    private const int DamageMultiplier = 64;

    private static ModifierInfo Mod(ModifierFunc func, ModifierDomain domain, int operation,
        int? modified = DamageMultiplier, int? modifying = DamageMultiplier, int? group = null, int? skill = null) =>
        new(func, domain, operation, modified, modifying, group, skill);

    private static DogmaFit Collect(FakeDogmaDataAccessor data, FitInput input)
    {
        var fit = new DogmaFitBuilder(data).Build(input);
        new DogmaEffectCollector(data).Collect(fit);
        return fit;
    }

    [Fact]
    public void LocationGroupModifier_RegistersOnMatchingGroupOnly_AndPenalises()
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(DamageMultiplier, 1.0, stackable: false)
            .Type(587, 25, 6)
            .Type(10190, 302, 7).TypeEffect(10190, 93)             // damage mod (Module category 7)
            .Type(2488, 74, 7)                                     // weapon, group 74
            .Type(2048, 60, 7)                                     // other module, group 60
            .Effect(93, 4, Mod(ModifierFunc.LocationGroupModifier, ModifierDomain.ShipId, 4, group: 74));

        var fit = Collect(data, new FitInput(587,
            [new ModuleInput(10190, ModuleState.Online), new ModuleInput(2488, ModuleState.Online), new ModuleInput(2048, ModuleState.Online)],
            SkillSource.AllLevelFive));

        var weapon = fit.Modules.Single(m => m.TypeId == 2488);
        Assert.True(weapon.TryGet(DamageMultiplier, out var attribute));
        var modifier = Assert.Single(attribute.Modifiers);
        Assert.Equal(EffectOperator.PostMul, modifier.Operator);
        Assert.Equal(10190, modifier.Source.TypeId);
        Assert.True(modifier.Penalize);                            // Module source (cat 7) is not exempt
        Assert.False(fit.Modules.Single(m => m.TypeId == 2048).TryGet(DamageMultiplier, out _));
    }

    [Fact]
    public void Penalise_False_WhenSourceCategoryExempt()
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(DamageMultiplier, 1.0, stackable: false)
            .Type(587, 25, 6)
            .Type(2488, 74, 7, new SdeDogmaAttribute(182, 3300))   // weapon requires skill 3300
            .Type(3300, 9000, 16).TypeEffect(3300, 500)            // skill (category 16 = exempt)
            .Effect(500, 0, Mod(ModifierFunc.LocationGroupModifier, ModifierDomain.ShipId, 4, modifying: 280, group: 74));

        var fit = Collect(data, new FitInput(587, [new ModuleInput(2488, ModuleState.Online)], SkillSource.AllLevelFive));

        var weapon = fit.Modules.Single(m => m.TypeId == 2488);
        Assert.True(weapon.TryGet(DamageMultiplier, out var attribute));
        var modifier = Assert.Single(attribute.Modifiers);
        Assert.False(modifier.Penalize);                           // skill category 16 is exempt
        Assert.Equal(3300, modifier.Source.TypeId);
        Assert.Equal(280, modifier.SourceAttributeId);
    }

    [Fact]
    public void ItemModifier_RegistersOnSelf()
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(DamageMultiplier, 1.0, stackable: true)     // stackable -> never penalised
            .Type(587, 25, 6)
            .Type(2488, 74, 7).TypeEffect(2488, 600)
            .Effect(600, 4, Mod(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 2));   // ModAdd

        var fit = Collect(data, new FitInput(587, [new ModuleInput(2488, ModuleState.Online)], SkillSource.AllLevelFive));

        var weapon = Assert.Single(fit.Modules);
        Assert.True(weapon.TryGet(DamageMultiplier, out var attribute));
        var modifier = Assert.Single(attribute.Modifiers);
        Assert.Equal(EffectOperator.ModAdd, modifier.Operator);
        Assert.False(modifier.Penalize);                           // ModAdd is not penalisable
        Assert.Same(weapon, modifier.Source);
    }

    [Fact]
    public void LocationRequiredSkillModifier_RegistersOnSkillRequiringItemsOnly()
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(DamageMultiplier, 1.0, stackable: false)
            .Type(587, 25, 6)
            .Type(2488, 74, 7, new SdeDogmaAttribute(182, 3300))   // requires skill 3300
            .Type(2048, 60, 7)                                     // requires nothing
            .Type(3300, 9000, 16).TypeEffect(3300, 1100)
            .Effect(1100, 0, Mod(ModifierFunc.LocationRequiredSkillModifier, ModifierDomain.ShipId, 4, modifying: 280, skill: 3300));

        var fit = Collect(data, new FitInput(587,
            [new ModuleInput(2488, ModuleState.Online), new ModuleInput(2048, ModuleState.Online)],
            SkillSource.AllLevelFive));

        Assert.True(fit.Modules.Single(m => m.TypeId == 2488).TryGet(DamageMultiplier, out var attribute));
        Assert.Single(attribute.Modifiers);
        Assert.False(fit.Modules.Single(m => m.TypeId == 2048).TryGet(DamageMultiplier, out _));
    }

    [Fact]
    public void OwnerRequiredSkillModifier_RegistersOnOwnedItemsRequiringSkill_CarrierFallback()
    {
        // null skillTypeID is the patch convention for "the carrying skill itself" (effect 1730). The bonus lands on the
        // char-owned drone that requires that skill, not on a module that does not.
        var data = new FakeDogmaDataAccessor()
            .Attribute(DamageMultiplier, 1.0, stackable: false)
            .Type(587, 25, 6)
            .Type(2185, 100, 18, new SdeDogmaAttribute(182, 33699))   // drone requires skill 33699
            .Type(2048, 60, 7)                                        // a module requiring nothing
            .Type(33699, 9000, 16).TypeEffect(33699, 1730)            // the carrying skill
            .Effect(1730, 0, Mod(ModifierFunc.OwnerRequiredSkillModifier, ModifierDomain.CharId, 6, modifying: 292, skill: null));

        var fit = Collect(data, new FitInput(587, [new ModuleInput(2048, ModuleState.Online)],
            SkillSource.AllLevelFive, [new DroneInput(2185, 5)]));

        var drone = Assert.Single(fit.Drones);
        Assert.True(drone.TryGet(DamageMultiplier, out var attribute));          // carrier (33699) resolved, drone requires it
        Assert.Equal(33699, Assert.Single(attribute.Modifiers).Source.TypeId);
        Assert.False(fit.Modules.Single().TryGet(DamageMultiplier, out _));      // module does not require the skill
    }

    [Fact]
    public void EffectStopper_IsSkipped()
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(DamageMultiplier, 1.0, stackable: false)
            .Type(587, 25, 6)
            .Type(2488, 74, 7).TypeEffect(2488, 700)
            .Effect(700, 0, Mod(ModifierFunc.EffectStopper, ModifierDomain.Target, ModifierInfo.NoOperation, modified: null, modifying: null));

        var fit = Collect(data, new FitInput(587, [new ModuleInput(2488, ModuleState.Online)], SkillSource.AllLevelFive));

        Assert.False(fit.Modules.Single().TryGet(DamageMultiplier, out _));
    }

    [Fact]
    public void Operation9_IsSkipped()
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(280, 0.0, stackable: false)
            .Type(587, 25, 6)
            .Type(2488, 74, 7).TypeEffect(2488, 800)
            .Effect(800, 0, Mod(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 9, modified: 280, modifying: 280));

        var fit = Collect(data, new FitInput(587, [new ModuleInput(2488, ModuleState.Online)], SkillSource.AllLevelFive));

        Assert.False(fit.Modules.Single().TryGet(280, out _));
    }

    [Fact]
    public void OtherId_RoutesBetweenModuleAndItsCharge()
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(64, 0.0, stackable: true)
            .Attribute(100, 0.0, stackable: true)
            .Type(587, 25, 6)
            .Type(2488, 74, 7, new SdeDogmaAttribute(64, 10)).TypeEffect(2488, 810)   // weapon
            .Type(999, 83, 8, new SdeDogmaAttribute(100, 3)).TypeEffect(999, 811)      // loaded charge (category 8)
            .Effect(810, 4, Mod(ModifierFunc.ItemModifier, ModifierDomain.OtherId, 2, modified: 64, modifying: 64))    // module -> charge
            .Effect(811, 0, Mod(ModifierFunc.ItemModifier, ModifierDomain.OtherId, 2, modified: 100, modifying: 100)); // charge -> module

        var fit = Collect(data, new FitInput(587,
            [new ModuleInput(2488, ModuleState.Active, ChargeTypeId: 999)], SkillSource.AllLevelFive));

        var weapon = fit.Modules.Single();
        var charge = weapon.Charge;
        Assert.NotNull(charge);
        Assert.True(charge.TryGet(64, out var chargeAttr));               // module's otherID modifier landed on the charge
        Assert.Equal(2488, Assert.Single(chargeAttr.Modifiers).Source.TypeId);
        Assert.True(weapon.TryGet(100, out var weaponAttr));              // charge's otherID modifier landed on the module
        Assert.Equal(999, Assert.Single(weaponAttr.Modifiers).Source.TypeId);
    }

    [Fact]
    public void OtherId_WithoutCharge_IsSkipped()
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(DamageMultiplier, 1.0, stackable: false)
            .Type(587, 25, 6)
            .Type(2488, 74, 7).TypeEffect(2488, 1000)
            .Effect(1000, 4, Mod(ModifierFunc.ItemModifier, ModifierDomain.OtherId, 4));

        var fit = Collect(data, new FitInput(587, [new ModuleInput(2488, ModuleState.Online)], SkillSource.AllLevelFive));

        Assert.False(fit.Modules.Single().TryGet(DamageMultiplier, out _));   // no paired charge (V-4)
    }

    [Theory]
    [InlineData(ModuleState.Online, false)]   // effect needs Active; module only Online -> skipped
    [InlineData(ModuleState.Active, true)]
    public void ActiveCategoryEffect_GatedByModuleState(ModuleState state, bool expectedRegistered)
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(DamageMultiplier, 1.0, stackable: false)
            .Type(587, 25, 6)
            .Type(10190, 302, 7).TypeEffect(10190, 900)
            .Type(2488, 74, 7)
            .Effect(900, 1, Mod(ModifierFunc.LocationGroupModifier, ModifierDomain.ShipId, 4, group: 74));   // category 1 = active

        var fit = Collect(data, new FitInput(587,
            [new ModuleInput(10190, state), new ModuleInput(2488, ModuleState.Online)],
            SkillSource.AllLevelFive));

        Assert.Equal(expectedRegistered, fit.Modules.Single(m => m.TypeId == 2488).TryGet(DamageMultiplier, out _));
    }

    [Fact]
    public void StructureId_Domain_RoutesToTheStructuresModules()
    {
        // A Structure-Management skill effect (and structure role bonuses) target the structure's modules via the
        // structureID domain, not shipID — e.g. Structure Electronic Systems reducing a Standup module's capacitor use.
        var data = new FakeDogmaDataAccessor()
            .Attribute(DamageMultiplier, 1.0, stackable: false)
            .Type(35832, groupId: 1657, categoryId: 65).TypeEffect(35832, 6400)   // structure carries the effect
            .Type(2488, 74, 7)                                                    // a Standup module (group 74)
            .Effect(6400, 0, Mod(ModifierFunc.LocationGroupModifier, ModifierDomain.StructureId, 6, group: 74));

        var fit = Collect(data, new FitInput(35832, [new ModuleInput(2488, ModuleState.Online)], SkillSource.AllLevelFive));

        Assert.True(fit.Modules.Single().TryGet(DamageMultiplier, out _));   // structureID LocationGroup reached the module
    }

    [Fact]
    public void BoosterSideEffect_IsSkipped_PrimaryStillRegisters()
    {
        // A booster (category 20) carries a primary bonus and a "booster<Stat>Penalty" side-effect. The side-effect is
        // off by default (matching in-game behaviour), so only the primary modifier reaches the ship.
        const int armorHp = 265;
        var data = new FakeDogmaDataAccessor()
            .Attribute(armorHp, 0.0, stackable: true)
            .Type(587, 25, 6, new SdeDogmaAttribute(armorHp, 1000))
            .Type(9999, 303, 20, new SdeDogmaAttribute(1000, -200), new SdeDogmaAttribute(1001, 500))   // a booster
            .TypeEffect(9999, 2735).TypeEffect(9999, 5901)
            .EffectNamed(2735, "boosterArmorHpPenalty", 0, Mod(ModifierFunc.ItemModifier, ModifierDomain.ShipId, 2, armorHp, 1000))
            .EffectNamed(5901, "boosterArmorHpBonus", 0, Mod(ModifierFunc.ItemModifier, ModifierDomain.ShipId, 2, armorHp, 1001));

        var fit = Collect(data, new FitInput(587, [], SkillSource.AllLevelFive, null, [new ImplantInput(9999)]));

        Assert.True(fit.Ship.TryGet(armorHp, out var attribute));
        var modifier = Assert.Single(attribute.Modifiers);    // the boosterArmorHpPenalty side-effect was skipped
        Assert.Equal(1001, modifier.SourceAttributeId);       // only the primary bonus remains
    }
}
