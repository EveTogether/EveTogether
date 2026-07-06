using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Pass 3 evaluation: operator folds (additive / multiplicative-with-stacking / assignment), recursive + memoised
/// source resolution, and the <strong>VC-01 acceptance</strong>: 2x MFS II + 2x Vortex Compact MFS
/// stack into one bucket per attribute x domain, not per type. With the real module values (1.1 and 1.08) the
/// combined damage multiplier is 1.27848 — far from the per-type bug's ~1.381.
/// </summary>
public class DogmaEvaluatorTests
{
    private const int DamageMultiplier = 64;

    private static ModifierInfo Mod(ModifierFunc func, ModifierDomain domain, int operation,
        int modified, int modifying, int? group = null, int? skill = null) =>
        new(func, domain, operation, modified, modifying, group, skill);

    private static (DogmaFit Fit, DogmaEvaluator Eval) Setup(FakeDogmaDataAccessor data, FitInput input)
    {
        var fit = new DogmaFitBuilder(data).Build(input);
        new DogmaEffectCollector(data).Collect(fit);
        return (fit, new DogmaEvaluator(data));
    }

    [Fact]
    public void Vc01_TwoMfsTwoVortex_StackInOneBucket()
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(DamageMultiplier, 1.0, stackable: false)
            .Type(587, 25, 6)
            .Type(10190, 302, 7, new SdeDogmaAttribute(DamageMultiplier, 1.1)).TypeEffect(10190, 93)   // MFS II
            .Type(11105, 303, 7, new SdeDogmaAttribute(DamageMultiplier, 1.08)).TypeEffect(11105, 93)  // Vortex Compact
            .Type(2488, 74, 7, new SdeDogmaAttribute(DamageMultiplier, 1.0))                           // hybrid weapon
            .Effect(93, 4, Mod(ModifierFunc.LocationGroupModifier, ModifierDomain.ShipId, 4, DamageMultiplier, DamageMultiplier, group: 74));

        var (fit, eval) = Setup(data, new FitInput(587,
            [
                new ModuleInput(10190, ModuleState.Online), new ModuleInput(10190, ModuleState.Online),
                new ModuleInput(11105, ModuleState.Online), new ModuleInput(11105, ModuleState.Online),
                new ModuleInput(2488, ModuleState.Online)
            ],
            SkillSource.AllLevelFive));

        var weapon = fit.Modules.Single(m => m.TypeId == 2488);
        var combined = eval.Resolve(weapon, DamageMultiplier);

        Assert.Equal(4, weapon.Attributes[DamageMultiplier].Modifiers.Count);   // one bucket, four modifiers
        Assert.Equal(1.27848, combined, 5);
        Assert.True(combined < 1.30);                                           // not the per-type bug (~1.381)
    }

    [Fact]
    public void ModAdd_And_ModSub_AreAdditiveOnBase()
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(100, 0.0, stackable: true)
            .Type(587, 25, 6)
            .Type(2488, 74, 7, new SdeDogmaAttribute(100, 50), new SdeDogmaAttribute(200, 30), new SdeDogmaAttribute(201, 5))
            .TypeEffect(2488, 610).TypeEffect(2488, 611)
            .Effect(610, 4, Mod(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 2, 100, 200))   // + attr200 (30)
            .Effect(611, 4, Mod(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 3, 100, 201));  // - attr201 (5)

        var (fit, eval) = Setup(data, new FitInput(587, [new ModuleInput(2488, ModuleState.Online)], SkillSource.AllLevelFive));

        Assert.Equal(75, eval.Resolve(fit.Modules.Single(), 100));   // 50 + 30 - 5
    }

    [Fact]
    public void StackableAttribute_MultipliersStackWithoutPenalty()
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(DamageMultiplier, 1.0, stackable: true)   // stackable -> no penalty
            .Type(587, 25, 6)
            .Type(10190, 302, 7, new SdeDogmaAttribute(DamageMultiplier, 1.1)).TypeEffect(10190, 93)
            .Type(2488, 74, 7, new SdeDogmaAttribute(DamageMultiplier, 1.0))
            .Effect(93, 4, Mod(ModifierFunc.LocationGroupModifier, ModifierDomain.ShipId, 4, DamageMultiplier, DamageMultiplier, group: 74));

        var (fit, eval) = Setup(data, new FitInput(587,
            [new ModuleInput(10190, ModuleState.Online), new ModuleInput(10190, ModuleState.Online), new ModuleInput(2488, ModuleState.Online)],
            SkillSource.AllLevelFive));

        Assert.Equal(1.21, eval.Resolve(fit.Modules.Single(m => m.TypeId == 2488), DamageMultiplier), 6);   // 1.1 * 1.1
    }

    [Fact]
    public void PostPercent_AppliesValueOverHundred()
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(100, 0.0, stackable: true)
            .Type(587, 25, 6)
            .Type(2488, 74, 7, new SdeDogmaAttribute(100, 200), new SdeDogmaAttribute(300, 10)).TypeEffect(2488, 620)
            .Effect(620, 4, Mod(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 6, 100, 300));   // +10%

        var (fit, eval) = Setup(data, new FitInput(587, [new ModuleInput(2488, ModuleState.Online)], SkillSource.AllLevelFive));

        Assert.Equal(220, eval.Resolve(fit.Modules.Single(), 100), 6);   // 200 * (1 + 10/100)
    }

    [Fact]
    public void PostAssign_PicksMaxWhenHighIsGood()
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(100, 0.0, stackable: true, highIsGood: true)
            .Type(587, 25, 6)
            .Type(2488, 74, 7, new SdeDogmaAttribute(100, 5), new SdeDogmaAttribute(301, 12)).TypeEffect(2488, 630)
            .Effect(630, 4, Mod(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 7, 100, 301));   // assign attr301 (12)

        var (fit, eval) = Setup(data, new FitInput(587, [new ModuleInput(2488, ModuleState.Online)], SkillSource.AllLevelFive));

        Assert.Equal(12, eval.Resolve(fit.Modules.Single(), 100));   // assigned value overrides base 5
    }

    [Fact]
    public void SourceValue_ResolvesRecursively_AndMemoises()
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(DamageMultiplier, 1.0, stackable: false)
            .Type(587, 25, 6)
            .Type(10190, 302, 7, new SdeDogmaAttribute(DamageMultiplier, 1.0), new SdeDogmaAttribute(900, 0.2))
            .TypeEffect(10190, 600).TypeEffect(10190, 93)
            .Type(2488, 74, 7, new SdeDogmaAttribute(DamageMultiplier, 1.0))
            .Effect(600, 4, Mod(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 2, DamageMultiplier, 900))           // module's own dmg-mult += 0.2 -> 1.2
            .Effect(93, 4, Mod(ModifierFunc.LocationGroupModifier, ModifierDomain.ShipId, 4, DamageMultiplier, DamageMultiplier, group: 74));

        var (fit, eval) = Setup(data, new FitInput(587,
            [new ModuleInput(10190, ModuleState.Online), new ModuleInput(2488, ModuleState.Online)], SkillSource.AllLevelFive));

        var module = fit.Modules.Single(m => m.TypeId == 10190);
        var weapon = fit.Modules.Single(m => m.TypeId == 2488);
        Assert.Equal(1.2, eval.Resolve(weapon, DamageMultiplier), 6);   // 1.0 * resolved module dmg-mult (1.2)
        Assert.Equal(1.2, module.Attributes[DamageMultiplier].Resolved); // memoised during the weapon's resolution
    }

    [Fact]
    public void MaxAttributeId_CapsResonance_AtPolarizedResistanceKiller()
    {
        const int shieldEmResonance = 271;
        const int shieldMaxResonance = 1528;
        const int resistanceKiller = 1978;
        var data = new FakeDogmaDataAccessor()
            .Attribute(shieldEmResonance, 1.0, stackable: false, highIsGood: false, maxAttributeId: shieldMaxResonance)
            .Attribute(shieldMaxResonance, 1.0, stackable: true)
            .Attribute(resistanceKiller, 0.0, stackable: true)
            .Type(587, 25, 6, new SdeDogmaAttribute(shieldEmResonance, 0.5))                            // ship: 50% EM resist
            .Type(34278, 74, 7, new SdeDogmaAttribute(resistanceKiller, 100)).TypeEffect(34278, 5995)   // Polarized weapon
            .Effect(5995, 0, Mod(ModifierFunc.ItemModifier, ModifierDomain.ShipId, 7, shieldEmResonance, resistanceKiller));

        var (fit, eval) = Setup(data, new FitInput(587, [new ModuleInput(34278, ModuleState.Online)], SkillSource.AllLevelFive));

        // PostAssign pushes the resonance to 100 (take 100x damage); maxAttributeID caps it at 1.0 (0% resist).
        Assert.Equal(1.0, eval.Resolve(fit.Ship, shieldEmResonance), 6);
    }

    [Fact]
    public void MaxAttributeId_LeavesValueBelowCapUntouched()
    {
        const int shieldEmResonance = 271;
        const int shieldMaxResonance = 1528;
        var data = new FakeDogmaDataAccessor()
            .Attribute(shieldEmResonance, 1.0, stackable: false, highIsGood: false, maxAttributeId: shieldMaxResonance)
            .Attribute(shieldMaxResonance, 1.0, stackable: true)
            .Type(587, 25, 6, new SdeDogmaAttribute(shieldEmResonance, 0.5));

        var (fit, eval) = Setup(data, new FitInput(587, [], SkillSource.AllLevelFive));

        Assert.Equal(0.5, eval.Resolve(fit.Ship, shieldEmResonance), 6);   // already under the 1.0 cap, unchanged
    }

    [Fact]
    public void ForcedSkillLevel_BypassesModifiers()
    {
        var data = new FakeDogmaDataAccessor()
            .Attribute(DogmaAttributeIds.SkillLevel, 0.0, stackable: false)
            .Type(587, 25, 6)
            .Type(2488, 74, 7, new SdeDogmaAttribute(182, 3300))
            .Type(3300, 9000, 16);

        var (fit, eval) = Setup(data, new FitInput(587, [new ModuleInput(2488, ModuleState.Online)], SkillSource.AllLevelFive));

        Assert.Equal(5, eval.Resolve(fit.Skills.Single(), DogmaAttributeIds.SkillLevel));   // forced, no pipeline
    }

    [Fact]
    public void PostDiv_WithZeroSource_ContributesNoChange_AndStaysFinite()
    {
        // A PostDiv divides the target by the source attribute's value. When the source is 0 (an offline/unloaded
        // module whose divisor attribute reads 0), the naive 1/0 yields Infinity/NaN and poisons the whole memoised
        // resolve chain (current *= 1 + delta). The fix makes a zero divisor a no-op (delta 0), so the target is
        // unchanged and finite.
        const int target = 100, divisor = 301;
        var data = new FakeDogmaDataAccessor()
            .Attribute(target, 0.0, stackable: true)
            .Type(587, 25, 6)
            .Type(2488, 74, 7, new SdeDogmaAttribute(target, 250), new SdeDogmaAttribute(divisor, 0)).TypeEffect(2488, 640)
            .Effect(640, 4, Mod(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 5, target, divisor));   // PostDiv by attr301 (0)

        var (fit, eval) = Setup(data, new FitInput(587, [new ModuleInput(2488, ModuleState.Online)], SkillSource.AllLevelFive));

        var resolved = eval.Resolve(fit.Modules.Single(), target);
        Assert.False(double.IsNaN(resolved));
        Assert.False(double.IsInfinity(resolved));
        Assert.Equal(250, resolved);   // zero divisor contributes no change, base value untouched
    }

    [Fact]
    public void PreDiv_WithZeroSource_ContributesNoChange_AndStaysFinite()
    {
        const int target = 100, divisor = 301;
        var data = new FakeDogmaDataAccessor()
            .Attribute(target, 0.0, stackable: true)
            .Type(587, 25, 6)
            .Type(2488, 74, 7, new SdeDogmaAttribute(target, 250), new SdeDogmaAttribute(divisor, 0)).TypeEffect(2488, 641)
            .Effect(641, 4, Mod(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 1, target, divisor));   // PreDiv by attr301 (0)

        var (fit, eval) = Setup(data, new FitInput(587, [new ModuleInput(2488, ModuleState.Online)], SkillSource.AllLevelFive));

        var resolved = eval.Resolve(fit.Modules.Single(), target);
        Assert.False(double.IsNaN(resolved));
        Assert.False(double.IsInfinity(resolved));
        Assert.Equal(250, resolved);
    }

    [Fact]
    public void Weather_InjectsEffectBeacon_AsShipAnchoredSource_OnlyWhenSelected()
    {
        // A Pulsar effect beacon (group 920, type 30844) carries shieldCapacityMultiplier (146) = 1.30 and a category-7
        // "system" effect that multiplies the ship's shieldCapacity (263) by it via the shipID domain — the SDE shape of
        // a real wormhole beacon. Selecting it injects the beacon as a ship-anchored source; not selecting it adds nothing.
        const int beacon = 30844, shieldCapacityMultiplier = 146;
        var data = new FakeDogmaDataAccessor()
            .Attribute(DogmaAttributeIds.ShieldCapacity, 0, stackable: true)
            .Type(587, 25, 6, new SdeDogmaAttribute(DogmaAttributeIds.ShieldCapacity, 1000))
            .Type(beacon, 920, 2, new SdeDogmaAttribute(shieldCapacityMultiplier, 1.30)).TypeEffect(beacon, 3992)
            .Effect(3992, 7, Mod(ModifierFunc.ItemModifier, ModifierDomain.ShipId, 4,
                DogmaAttributeIds.ShieldCapacity, shieldCapacityMultiplier));

        var (noWeather, evalNo) = Setup(data, new FitInput(587, [], SkillSource.AllLevelFive));
        Assert.Equal(1000, evalNo.Resolve(noWeather.Ship, DogmaAttributeIds.ShieldCapacity), 6);   // source absent

        var (withWeather, evalYes) = Setup(data,
            new FitInput(587, [], SkillSource.AllLevelFive, Weather: new WeatherInput(beacon)));
        Assert.Equal(1300, evalYes.Resolve(withWeather.Ship, DogmaAttributeIds.ShieldCapacity), 6);   // 1000 * 1.30
    }
}
