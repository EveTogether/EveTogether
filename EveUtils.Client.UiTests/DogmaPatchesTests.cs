using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Sde.Storage;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The data-driven patches over the SDE. The accessor applies effect patches only where the SDE effect is empty (or
/// the effect is a new custom id), so the table must describe exactly the intended routing.
/// </summary>
public class DogmaPatchesTests
{
    [Fact]
    public void Effect1730_DroneDamageBonus_InjectsOwnerSkillDamageMultiplierModifier()
    {
        Assert.True(DogmaPatches.TryGetEffectPatch(1730, out var patch));
        var modifier = Assert.Single(patch.Modifiers);
        Assert.Equal(ModifierFunc.OwnerRequiredSkillModifier, modifier.Func);
        Assert.Equal(ModifierDomain.CharId, modifier.Domain);
        Assert.Equal(6, modifier.Operation);                 // post-percent
        Assert.Equal(64, modifier.ModifiedAttributeId);      // damageMultiplier
        Assert.Equal(292, modifier.ModifyingAttributeId);    // the skill's level-scaled damageMultiplierBonus
        Assert.Null(modifier.SkillTypeId);                   // carrier convention: the skill that holds the effect
    }

    [Fact]
    public void Afterburner_AddsShipMassOnly()
    {
        // The velocity boost moved to a code aggregate; the data-driven patch only adds the prop module's mass.
        Assert.True(DogmaPatches.TryGetEffectPatch(6731, out var patch));
        var modifier = Assert.Single(patch.Modifiers);
        Assert.True(modifier is { ModifiedAttributeId: 4, ModifyingAttributeId: 796, Operation: 2 });   // mass += massAddition
    }

    [Fact]
    public void Microwarpdrive_AddsMassAndPenalisesSignatureRadius()
    {
        Assert.True(DogmaPatches.TryGetEffectPatch(6730, out var patch));
        Assert.Contains(patch.Modifiers, m => m is { ModifiedAttributeId: 4, ModifyingAttributeId: 796, Operation: 2 });     // mass += massAddition
        Assert.Contains(patch.Modifiers, m => m is { ModifiedAttributeId: 552, ModifyingAttributeId: 554, Operation: 6 });   // signatureRadius post-percent
        Assert.DoesNotContain(patch.Modifiers, m => m.ModifiedAttributeId == 65532);                                          // no velocityBoost (code aggregate now)
    }

    [Fact]
    public void VelocityBoost_IsNoLongerPatched()
    {
        // Propulsion velocity is a code aggregate now — the synthetic velocityBoost attribute/effect are gone.
        Assert.DoesNotContain(DogmaPatches.Attributes, a => a.AttributeId == 65532);
        Assert.Null(DogmaPatches.AttributeMeta(65532));
        Assert.False(DogmaPatches.TryGetEffectPatch(65532, out _));
    }

    [Fact]
    public void TypeLinks_AttachAlignTimeToShipsAndCpuPowerLoadToModules()
    {
        Assert.Contains(65534, DogmaPatches.EffectIdsForCategory(6));      // ships: alignTime
        Assert.DoesNotContain(65532, DogmaPatches.EffectIdsForCategory(6)); // velocityBoost removed
        Assert.Contains(65521, DogmaPatches.EffectIdsForCategory(7));      // modules: cpuPowerLoad
    }

    [Fact]
    public void CpuPowerLoad_OnlineEffect_FoldsModuleCpuAndPowerOntoShipLoad()
    {
        // The custom online effect that makes a module's cpu/power count against the ship only while it is online.
        Assert.True(DogmaPatches.TryGetEffectPatch(65521, out var patch));
        Assert.Equal(4, patch.EffectCategoryId);             // online: gated off when a module is offline
        Assert.Contains(patch.Modifiers, m => m is { ModifiedAttributeId: 49, ModifyingAttributeId: 50, Operation: 2 });   // cpuLoad += cpu
        Assert.Contains(patch.Modifiers, m => m is { ModifiedAttributeId: 15, ModifyingAttributeId: 30, Operation: 2 });   // powerLoad += power
        Assert.All(patch.Modifiers, m => Assert.Equal(ModifierDomain.ShipId, m.Domain));
    }

    [Fact]
    public void AlignTime_SyntheticAttributesAndEffectFoldTheFormula()
    {
        // alignTime base = -ln(0.25); the effect multiplies in agility (70) and mass (4) and divides by a million (65531).
        var alignMeta = DogmaPatches.AttributeMeta(65534)!;
        Assert.Equal(1.3862943611198906, alignMeta.DefaultValue, 12);
        Assert.True(alignMeta.Stackable);          // so the multiplications are not stacking-penalised
        Assert.False(alignMeta.HighIsGood);        // lower align time is better
        Assert.Equal(1_000_000, DogmaPatches.AttributeMeta(65531)!.DefaultValue);

        Assert.True(DogmaPatches.TryGetEffectPatch(65534, out var patch));
        Assert.Contains(patch.Modifiers, m => m is { ModifiedAttributeId: 65534, ModifyingAttributeId: 70, Operation: 4 });    // alignTime *= agility
        Assert.Contains(patch.Modifiers, m => m is { ModifiedAttributeId: 65534, ModifyingAttributeId: 4, Operation: 4 });     // alignTime *= mass
        Assert.Contains(patch.Modifiers, m => m is { ModifiedAttributeId: 65534, ModifyingAttributeId: 65531, Operation: 5 }); // alignTime /= million
    }

    [Fact]
    public void MissileSizeSkill_DamageBonus_IsAnOwnerSkillPostPercentPerType()
    {
        Assert.True(DogmaPatches.TryGetEffectPatch(668, out var kinetic));   // missileKineticDmgBonus2
        var modifier = Assert.Single(kinetic.Modifiers);
        Assert.Equal(ModifierFunc.OwnerRequiredSkillModifier, modifier.Func);
        Assert.Equal(117, modifier.ModifiedAttributeId);     // kinetic
        Assert.Equal(292, modifier.ModifyingAttributeId);    // level-scaled damageMultiplierBonus
        Assert.Equal(6, modifier.Operation);
        Assert.Null(modifier.SkillTypeId);                   // carrier convention
    }

    [Fact]
    public void MissileSpecialization_SelfRof_ReducesCycleViaCarrier()
    {
        Assert.True(DogmaPatches.TryGetEffectPatch(1851, out var rof));      // selfRof, shared by all missile specs
        var modifier = Assert.Single(rof.Modifiers);
        Assert.Equal(ModifierFunc.LocationRequiredSkillModifier, modifier.Func);
        Assert.Equal(51, modifier.ModifiedAttributeId);      // cycle time
        Assert.Equal(293, modifier.ModifyingAttributeId);    // rofBonus
        Assert.Null(modifier.SkillTypeId);                   // carrier convention
    }
}
