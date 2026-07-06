using EveUtils.Shared.Modules.Sde.Enums;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Sde.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Pass 4 + the calculator facade: fitting resources (CPU/power used vs output, rounded), max velocity and uniform
/// EHP, plus the all-level-5 path — every skill is injected and a ship-wide skill bonus (CPU Management's shape:
/// per-level bonus attribute, then a PostPercent on the ship) flows through to the output.
/// </summary>
public class DogmaCalculatorTests
{
    private static FitResult Calculate(FakeDogmaDataAccessor data, FitInput input)
    {
        var evaluator = new DogmaEvaluator(data);
        var calculator = new DogmaCalculator(data, new DogmaFitBuilder(data), new DogmaEffectCollector(data),
            new ReactiveArmorHardener(data, evaluator), new DerivedStatsCalculator(evaluator, data), evaluator);
        return calculator.CalculateAsync(input).GetAwaiter().GetResult();
    }

    // Wires the cpuPowerLoad online effect (65521) onto a module type the way DogmaPatches does for the real accessor
    // (the fake has no patch layer): each online module folds its cpu/power onto the ship's cpuLoad/powerLoad.
    private static FakeDogmaDataAccessor WithCpuPowerLoad(FakeDogmaDataAccessor data, int moduleTypeId) =>
        data.TypeEffect(moduleTypeId, 65521)
            .Effect(65521, 4,
                new ModifierInfo(ModifierFunc.ItemModifier, ModifierDomain.ShipId, 2, DogmaAttributeIds.CpuLoad, DogmaAttributeIds.Cpu, null, null),
                new ModifierInfo(ModifierFunc.ItemModifier, ModifierDomain.ShipId, 2, DogmaAttributeIds.PowerLoad, DogmaAttributeIds.Power, null, null));

    [Fact]
    public void Calculate_CpuPowerVelocity_FromShipAndModules()
    {
        var data = WithCpuPowerLoad(new FakeDogmaDataAccessor()
            .Type(587, 25, 6,
                new SdeDogmaAttribute(DogmaAttributeIds.CpuOutput, 212.5),
                new SdeDogmaAttribute(DogmaAttributeIds.PowerOutput, 96.2),
                new SdeDogmaAttribute(DogmaAttributeIds.MaxVelocity, 714))
            .Type(2488, 74, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.Cpu, 30),
                new SdeDogmaAttribute(DogmaAttributeIds.Power, 13.05)), 2488);

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(2488, ModuleState.Online), new ModuleInput(2488, ModuleState.Online)],
            SkillSource.AllLevelFive));

        Assert.Equal(60, result.Derived.CpuUsed);          // 2 × 30 onto the ship's cpuLoad
        Assert.Equal(26.1, result.Derived.PowerUsed);      // 2 × 13.05, rounded to 2 decimals
        Assert.Equal(212.5, result.Derived.CpuOutput);
        Assert.Equal(96.2, result.Derived.PowerOutput);
        Assert.Equal(714, result.Derived.MaxVelocity);
    }

    [Fact]
    public void CpuPowerLoad_OfflineModule_FreesItsFittingLoad()
    {
        // The online-quirk: cpu/power only count while a module is online or higher. The same module offline
        // (passive) contributes nothing, so offlining frees fitting resources — the in-game behaviour the sim needs.
        var data = WithCpuPowerLoad(new FakeDogmaDataAccessor()
            .Type(587, 25, 6,
                new SdeDogmaAttribute(DogmaAttributeIds.CpuOutput, 100),
                new SdeDogmaAttribute(DogmaAttributeIds.PowerOutput, 100))
            .Type(2488, 74, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.Cpu, 30),
                new SdeDogmaAttribute(DogmaAttributeIds.Power, 13)), 2488);

        var online = Calculate(data, new FitInput(587, [new ModuleInput(2488, ModuleState.Online)], SkillSource.AllLevelFive));
        Assert.Equal(30, online.Derived.CpuUsed);
        Assert.Equal(13, online.Derived.PowerUsed);

        var offline = Calculate(data, new FitInput(587, [new ModuleInput(2488, ModuleState.Passive)], SkillSource.AllLevelFive));
        Assert.Equal(0, offline.Derived.CpuUsed);
        Assert.Equal(0, offline.Derived.PowerUsed);
    }

    [Fact]
    public void Calculate_Ehp_UniformProfile()
    {
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6,
                new SdeDogmaAttribute(DogmaAttributeIds.ShieldCapacity, 1000),
                new SdeDogmaAttribute(271, 1.0), new SdeDogmaAttribute(274, 1.0),
                new SdeDogmaAttribute(273, 0.5), new SdeDogmaAttribute(272, 0.5));

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive));

        // mean shield resonance = (1 + 1 + 0.5 + 0.5) / 4 = 0.75 -> 1000 / 0.75
        Assert.Equal(1333.33, result.Derived.ShieldEhp, 2);
        Assert.Equal(0, result.Derived.ArmorEhp);          // no armor hp seeded
        Assert.Equal(result.Derived.ShieldEhp, result.Derived.Ehp, 6);
    }

    [Fact]
    public void Calculate_Ehp_RawProfile_IgnoresResists()
    {
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6,
                new SdeDogmaAttribute(DogmaAttributeIds.ShieldCapacity, 1000),
                new SdeDogmaAttribute(271, 1.0), new SdeDogmaAttribute(274, 1.0),
                new SdeDogmaAttribute(273, 0.5), new SdeDogmaAttribute(272, 0.5));

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive,
            Profile: new DamageProfile(0, 0, 0, 0)));

        // All-zero profile = "Raw HP": resists are ignored, so EHP is the raw buffer (1000), not 1333.33.
        Assert.Equal(1000, result.Derived.ShieldEhp, 6);
        Assert.Equal(result.Derived.ShieldEhp, result.Derived.Ehp, 6);
    }

    [Fact]
    public void AllLevelFive_AppliesShipWideSkillBonus_ToCpuOutput()
    {
        // CPU Management's real shape: the skill turns a per-level bonus attribute into a PostPercent on cpuOutput.
        const int cpuManagement = 3426;
        const int bonusAttribute = 424;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6, new SdeDogmaAttribute(DogmaAttributeIds.CpuOutput, 100))
            .Type(cpuManagement, 9000, 16, new SdeDogmaAttribute(bonusAttribute, 5))   // 5% per level
            .TypeEffect(cpuManagement, 700).TypeEffect(cpuManagement, 701)
            .Effect(700, 0, new ModifierInfo(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 0, bonusAttribute, DogmaAttributeIds.SkillLevel, null, null))  // bonus *= level
            .Effect(701, 0, new ModifierInfo(ModifierFunc.ItemModifier, ModifierDomain.ShipId, 6, DogmaAttributeIds.CpuOutput, bonusAttribute, null, null)); // ship cpuOutput += bonus%

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive));

        Assert.Equal(125, result.Derived.CpuOutput, 6);    // 100 * (1 + (5 × 5) / 100)
    }

    [Fact]
    public void Propulsion_BoostsVelocityPerActivePropModule_WhenActive()
    {
        // Velocity is a code aggregate: each active prop module boosts maxVelocity by speedFactor*speedBoostFactor
        // /mass (a percentage). Mass seeded directly (the fake has no mass-addition patch). 270 * (1 + 156.25*1.5M/2.1M/100)
        // = 571.34.
        const int afterburner = 6001;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6,
                new SdeDogmaAttribute(DogmaAttributeIds.MaxVelocity, 270),
                new SdeDogmaAttribute(DogmaAttributeIds.Mass, 2_100_000))
            .Type(afterburner, 46, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.SpeedFactor, 156.25),
                new SdeDogmaAttribute(DogmaAttributeIds.SpeedBoostFactor, 1_500_000));

        var active = Calculate(data, new FitInput(587, [new ModuleInput(afterburner, ModuleState.Active)], SkillSource.AllLevelFive));
        Assert.Equal(571.34, active.Derived.MaxVelocity, 2);

        // Online (not active) -> no boost.
        var online = Calculate(data, new FitInput(587, [new ModuleInput(afterburner, ModuleState.Online)], SkillSource.AllLevelFive));
        Assert.Equal(270, online.Derived.MaxVelocity);
    }

    [Fact]
    public void Propulsion_MultiplePropModules_StackingPenalised()
    {
        // Two identical prop modules each boost +100% (100*1M/1M); the second is stacking-penalised (Factor^1 = 0.86912):
        // 100 * (1 + 1.0) * (1 + 1.0 * 0.8691199808003974) = 373.82. This is the multi-prop case the old pooled patch broke.
        const int prop = 6001;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6,
                new SdeDogmaAttribute(DogmaAttributeIds.MaxVelocity, 100),
                new SdeDogmaAttribute(DogmaAttributeIds.Mass, 1_000_000))
            .Type(prop, 46, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.SpeedFactor, 100),
                new SdeDogmaAttribute(DogmaAttributeIds.SpeedBoostFactor, 1_000_000));

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(prop, ModuleState.Active), new ModuleInput(prop, ModuleState.Active)], SkillSource.AllLevelFive));

        Assert.Equal(373.82, result.Derived.MaxVelocity, 2);
    }

    [Fact]
    public void Microwarpdrive_PenalisesSignatureRadius()
    {
        // The MWD signature penalty stays data-driven (signatureRadius *(1 + sigBonus/100)); wired manually here since the
        // fake has no patch layer. sigBonus 500 -> 125 * 6 = 750. Velocity comes from the code aggregate.
        const int microwarpdrive = 6002;
        const int signatureRadiusBonus = 554;
        ModifierInfo Ship(int op, int modified, int modifying) =>
            new(ModifierFunc.ItemModifier, ModifierDomain.ShipId, op, modified, modifying, null, null);

        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6,
                new SdeDogmaAttribute(DogmaAttributeIds.MaxVelocity, 270),
                new SdeDogmaAttribute(DogmaAttributeIds.SignatureRadius, 125),
                new SdeDogmaAttribute(DogmaAttributeIds.Mass, 2_100_000))
            .Type(microwarpdrive, 46, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.SpeedFactor, 156.25),
                new SdeDogmaAttribute(DogmaAttributeIds.SpeedBoostFactor, 1_500_000),
                new SdeDogmaAttribute(signatureRadiusBonus, 500))
            .TypeEffect(microwarpdrive, 6730)
            .Effect(6730, 1, Ship(6, DogmaAttributeIds.SignatureRadius, signatureRadiusBonus));   // active: sig *(1 + sigBonus/100)

        var result = Calculate(data, new FitInput(587, [new ModuleInput(microwarpdrive, ModuleState.Active)], SkillSource.AllLevelFive));

        Assert.Equal(571.34, result.Derived.MaxVelocity, 2);
        Assert.Equal(750, result.Derived.SignatureRadius, 6);
    }

    [Fact]
    public void AlignTime_FoldsConstantTimesAgilityTimesMassOverMillion()
    {
        // The data-driven align-time pattern (DogmaPatches): the synthetic alignTime attribute starts at -ln(0.25); a
        // passive ship effect multiplies in agility and mass and divides by a million. Wired manually here because the
        // fake accessor has no patch layer. agility 0.6, mass 2,000,000 -> 1.3862943611198906 * 0.6 * 2 = 1.66355.
        const int agility = 70;
        const int alignTime = 65534;
        const int million = 65531;
        const double constant = 1.3862943611198906;
        ModifierInfo Self(int op, int modified, int modifying) =>
            new(ModifierFunc.ItemModifier, ModifierDomain.ItemId, op, modified, modifying, null, null);

        var data = new FakeDogmaDataAccessor()
            .Attribute(alignTime, constant, stackable: true)
            .Attribute(million, 1_000_000, stackable: true)
            .Type(587, 25, 6,
                new SdeDogmaAttribute(agility, 0.6),
                new SdeDogmaAttribute(DogmaAttributeIds.Mass, 2_000_000))
            .TypeEffect(587, alignTime)
            .Effect(alignTime, 0,
                Self(4, alignTime, agility),     // alignTime *= agility
                Self(4, alignTime, DogmaAttributeIds.Mass),   // alignTime *= mass
                Self(5, alignTime, million));    // alignTime /= million

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive));

        Assert.Equal(1.66355, result.Derived.AlignTime, 5);
    }

    [Fact]
    public void TurretDps_FromChargeDamageTimesMultiplierOverCycleTime()
    {
        const int turret = 2977;
        const int ammo = 21898;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(turret, 74, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.DamageMultiplier, 2.0),
                new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 10000))
            .Type(ammo, 83, 8,
                new SdeDogmaAttribute(114, 10), new SdeDogmaAttribute(116, 2),
                new SdeDogmaAttribute(117, 1), new SdeDogmaAttribute(118, 0));

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(turret, ModuleState.Active, ChargeTypeId: ammo)], SkillSource.AllLevelFive));

        Assert.Equal(2.6, result.Derived.TurretDps, 6);   // (10+2+1+0) * 2.0 / (10000 / 1000)
    }

    [Fact]
    public void TurretDps_OnlyCountsActiveTurrets_ZeroWhenOnlinedOrOfflined()
    {
        // A turret only deals damage while active; onlining or offlining it drops its DPS to zero — the same state-gate
        // as mining. Without it, disabling a gun left the turret DPS unchanged.
        const int turret = 2977, ammo = 21898;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(turret, 74, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.DamageMultiplier, 2.0),
                new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 10000))
            .Type(ammo, 83, 8,
                new SdeDogmaAttribute(114, 10), new SdeDogmaAttribute(116, 2),
                new SdeDogmaAttribute(117, 1), new SdeDogmaAttribute(118, 0));

        double Dps(ModuleState state) => Calculate(data, new FitInput(587,
            [new ModuleInput(turret, state, ChargeTypeId: ammo)], SkillSource.AllLevelFive)).Derived.TurretDps;

        Assert.Equal(2.6, Dps(ModuleState.Active), 6);   // a firing turret deals damage
        Assert.Equal(0, Dps(ModuleState.Online), 6);     // onlined but not cycling -> nothing
        Assert.Equal(0, Dps(ModuleState.Passive), 6);    // offlined -> nothing
    }

    [Fact]
    public void MissileDps_OnlyCountsActiveLaunchers_ZeroWhenOnlinedOrOfflined()
    {
        const int launcher = 2410, ammo = 209;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(launcher, 509, 7, new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 10000))
            .Type(ammo, 386, 8, new SdeDogmaAttribute(117, 120));

        double Dps(ModuleState state) => Calculate(data, new FitInput(587,
            [new ModuleInput(launcher, state, ChargeTypeId: ammo)], SkillSource.AllLevelFive)).Derived.MissileDps;

        Assert.Equal(12, Dps(ModuleState.Active), 6);
        Assert.Equal(0, Dps(ModuleState.Online), 6);
        Assert.Equal(0, Dps(ModuleState.Passive), 6);
    }

    [Fact]
    public void NonTurretWithCharge_ContributesNoDps()
    {
        const int scriptedModule = 5000;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(scriptedModule, 60, 7, new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 5000))   // no damageMultiplier
            .Type(21898, 83, 8, new SdeDogmaAttribute(114, 10));

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(scriptedModule, ModuleState.Active, ChargeTypeId: 21898)], SkillSource.AllLevelFive));

        Assert.Equal(0, result.Derived.TurretDps);
    }

    [Fact]
    public void DroneDps_FromDroneDamageTimesMultiplierOverCycleTimeAndCount()
    {
        const int drone = 2185;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(drone, 100, 18,                                          // category 18 = Drone
                new SdeDogmaAttribute(118, 32),                            // thermal damage
                new SdeDogmaAttribute(DogmaAttributeIds.DamageMultiplier, 1.92),
                new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 4000));

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, [new DroneInput(drone, 5)]));

        Assert.Equal(76.8, result.Derived.DroneDps, 6);    // 32 * 1.92 / (4000 / 1000) * 5
    }

    [Fact]
    public void FighterDps_FromAttackAbility_TimesActiveFighterCount()
    {
        const int fighter = 23055;   // Templar I — a light attack squadron
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(fighter, 1652, 87,                                       // category 87 = Fighter, group 1652 = Light Fighter
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackEm, 100),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackThermal, 20),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackDamageMultiplier, 1.5),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackDuration, 4000));

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, Fighters: [new FighterInput(fighter, 6)]));

        Assert.Equal(270.0, result.Derived.FighterDps, 6);    // (100 + 20) * 1.5 / (4000/1000) * 6 active fighters
    }

    [Fact]
    public void FighterDps_SumsAttackAndSecondarySalvo()
    {
        const int fighter = 32325;   // a heavy fighter: primary attack + secondary missile salvo
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(fighter, 1653, 87,
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackKinetic, 200),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackDamageMultiplier, 1.0),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackDuration, 5000),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterMissilesExplosive, 1000),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterMissilesDamageMultiplier, 1.0),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterMissilesDuration, 10000));

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, Fighters: [new FighterInput(fighter, 6)]));

        // attack 200*1/5 = 40 + salvo 1000*1/10 = 100 → 140 per fighter × 6
        Assert.Equal(840.0, result.Derived.FighterDps, 6);
    }

    [Fact]
    public void FighterDps_SupportSquadronWithoutAttackMultiplier_IsZero()
    {
        const int fighter = 37599;   // a support squadron: damage attrs may exist but no attack multiplier
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(fighter, 1537, 87,
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackEm, 50));   // no FighterAttackDamageMultiplier

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, Fighters: [new FighterInput(fighter, 3)]));

        Assert.Equal(0, result.Derived.FighterDps, 6);
    }

    [Fact]
    public void FighterDps_AppliesFightersSkillDamageBonus_ViaOwnerRequiredSkill()
    {
        // The Fighters skill (+5%/lvl fighter damage) routes via OwnerRequiredSkillModifier to the char-owned fighter —
        // the same mechanism as drone damage skills. Proves the fighters are registered as owned items (without that
        // wiring the bonus never reaches them and the DPS stays at the unbonused 100).
        const int fightersSkill = 23069;
        const int bonusAttribute = 9000;
        const int fighter = 23055;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(fighter, 1652, 87,
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackEm, 100),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackDamageMultiplier, 1.0),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackDuration, 1000),
                new SdeDogmaAttribute(DogmaAttributeIds.RequiredSkill[0], fightersSkill))   // the fighter requires Fighters
            .Type(fightersSkill, 9000, 16, new SdeDogmaAttribute(bonusAttribute, 5))         // 5% per level
            .TypeEffect(fightersSkill, 800).TypeEffect(fightersSkill, 801)
            .Effect(800, 0, new ModifierInfo(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 0, bonusAttribute, DogmaAttributeIds.SkillLevel, null, null))      // bonus *= level
            .Effect(801, 0, new ModifierInfo(ModifierFunc.OwnerRequiredSkillModifier, ModifierDomain.CharId, 6, DogmaAttributeIds.FighterAttackDamageMultiplier, bonusAttribute, null, null));   // fighter damageMult += bonus%

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, Fighters: [new FighterInput(fighter, 1)]));

        Assert.Equal(125.0, result.Derived.FighterDps, 6);    // 100 * (1 + (5×5)/100) / 1 × 1 active fighter
    }

    [Fact]
    public void FighterDpsSustained_PenalisesBothAbilities_ByTheSharedSalvoRearm()
    {
        const int fighter = 23055;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(fighter, 1652, 87,
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackEm, 100),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackDamageMultiplier, 1.0),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackDuration, 5000),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterMissilesKinetic, 140),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterMissilesDamageMultiplier, 1.0),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterMissilesDuration, 14000),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterSquadronRole, 2),       // 12 shots, 4000 ms rearm
                new SdeDogmaAttribute(DogmaAttributeIds.FighterRefuelTime, 5000));

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, Fighters: [new FighterInput(fighter, 1)]));

        Assert.Equal(30.0, result.Derived.FighterDps, 6);             // burst: 100/5 + 140/14 = 20 + 10
        // window 12×14000=168000, refuel 5000+4000×12=53000, sequence 221000; attack ceil(168000/5000)=34 shots:
        // 20×(34×5000)/221000 + 10×168000/221000
        Assert.Equal(22.986, result.Derived.FighterDpsSustained, 3);
    }

    [Fact]
    public void FighterDpsSustained_WithoutReloadingSalvo_EqualsBurst()
    {
        const int fighter = 23055;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(fighter, 1652, 87,
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackEm, 100),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackDamageMultiplier, 1.0),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackDuration, 5000),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterSquadronRole, 2),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterRefuelTime, 5000));

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, Fighters: [new FighterInput(fighter, 1)]));

        Assert.Equal(20.0, result.Derived.FighterDps, 6);
        Assert.Equal(20.0, result.Derived.FighterDpsSustained, 6);    // no reloading salvo → the attack sustains its burst
    }

    [Fact]
    public void FighterContributions_PerFighterDpsTimesActive_EqualsAggregate_AndSurfacesRange()
    {
        const int fighter = 23055;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(fighter, 1652, 87,
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackEm, 100),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackDamageMultiplier, 1.5),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackDuration, 4000),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackOptimalRange, 30000),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterAttackFalloffRange, 10000));

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, Fighters: [new FighterInput(fighter, 6)]));

        var contribution = Assert.Single(result.FighterContributions);
        Assert.Equal(fighter, contribution.TypeId);
        Assert.Equal(37.5, contribution.DpsPerFighter, 6);              // 100 * 1.5 / (4000/1000) per fighter
        Assert.Equal(37.5 * 6, result.Derived.FighterDps, 6);          // × 6 active fighters = the aggregate
        Assert.Equal(30000, contribution.OptimalRange, 6);
        Assert.Equal(10000, contribution.FalloffRange, 6);
    }

    [Fact]
    public void FighterContributions_SupportFighter_SurfacesEwar_NotDamage()
    {
        const int fighter = 40345;   // an ECM support fighter
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(fighter, 1537, 87,
                new SdeDogmaAttribute(DogmaAttributeIds.FighterEcmStrength, 2.4),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterEcmOptimal, 5000),
                new SdeDogmaAttribute(DogmaAttributeIds.FighterEcmFalloff, 5625));

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, Fighters: [new FighterInput(fighter, 3)]));

        Assert.Equal(0, result.Derived.FighterDps, 6);                 // support squadrons deal no damage
        var contribution = Assert.Single(result.FighterContributions);
        Assert.Equal(0, contribution.DpsPerFighter, 6);
        Assert.NotNull(contribution.Ewar);
        Assert.Equal(FighterEwarKind.Ecm, contribution.Ewar!.Kind);
        Assert.Equal(2.4, contribution.Ewar.Strength, 6);
        Assert.Equal(5000, contribution.Ewar.OptimalRange, 6);
    }

    [Fact]
    public void Drones_CappedByBandwidthAndFive()
    {
        const int drone = 2185;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6, new SdeDogmaAttribute(DogmaAttributeIds.DroneBandwidth, 25))   // ship: 25 Mbit/s available
            .Type(drone, 100, 18,
                new SdeDogmaAttribute(118, 10),                                              // 10 thermal
                new SdeDogmaAttribute(DogmaAttributeIds.DamageMultiplier, 1.0),
                new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 1000),
                new SdeDogmaAttribute(DogmaAttributeIds.DroneBandwidthUse, 10));             // 10 Mbit/s each

        // 5 in the bay, but only 25 / 10 = 2 fit the bandwidth (under the 5 limit).
        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, [new DroneInput(drone, 5)]));

        Assert.Equal(20, result.Derived.DroneDps, 6);            // 10 × 1.0 / 1s × 2 active
        Assert.Equal(20, result.Derived.DroneBandwidthUsed, 6); // not the 50 of all five
        Assert.Equal(2, result.Derived.DroneActiveCount);
    }

    [Fact]
    public void RemoteAssistance_Includes_LogisticDrones()
    {
        const int logiDrone = 23711;   // Light Armor Maintenance Bot I (group 640 Logistic Drone)
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(logiDrone, 640, 18,                                          // category 18 = Drone, group 640 = Logistic Drone
                new SdeDogmaAttribute(DogmaAttributeIds.ArmorRepairAmount, 12),
                new SdeDogmaAttribute(DogmaAttributeIds.Duration, 5000),
                new SdeDogmaAttribute(DogmaAttributeIds.MaxRange, 5000));

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, [new DroneInput(logiDrone, 5)]));

        // 12 HP / (5000/1000 s) = 2.4 HP/s per drone × 5 drones = 12 HP/s remote armor rep.
        Assert.Equal(12.0, result.Derived.RemoteArmorRepPerSec, 6);
        Assert.Equal(5000, result.Derived.RemoteArmorRangeMeters, 6);
        Assert.True(result.Derived.HasRemoteAssistance);
        Assert.Equal(0, result.Derived.DroneDps, 6);   // a logi drone deals no damage
    }

    [Fact]
    public void OwnerRequiredSkillModifier_BoostsOwnedDroneDamageMultiplier()
    {
        // The full owner-skill path the real drone-damage bonuses use (effect 6663 + the patched 1730): a skill turns a
        // per-level bonus attribute into a PostPercent on the damage multiplier of char-owned items requiring that skill.
        const int drone = 2185;
        const int droneSkill = 3436;
        const int bonus = 292;
        var data = new FakeDogmaDataAccessor()
            .Attribute(DogmaAttributeIds.DamageMultiplier, 1.0, stackable: false)
            .Attribute(bonus, 0, stackable: true)
            .Type(587, 25, 6)
            .Type(drone, 100, 18,
                new SdeDogmaAttribute(118, 10),
                new SdeDogmaAttribute(DogmaAttributeIds.DamageMultiplier, 2.0),
                new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 1000),
                new SdeDogmaAttribute(182, droneSkill))                    // drone requires the skill
            .Type(droneSkill, 9000, 16, new SdeDogmaAttribute(bonus, 10))  // +10% per level
            .TypeEffect(droneSkill, 146).TypeEffect(droneSkill, 6663)
            .Effect(146, 0, new ModifierInfo(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 0, bonus, DogmaAttributeIds.SkillLevel, null, null))                  // bonus *= level
            .Effect(6663, 0, new ModifierInfo(ModifierFunc.OwnerRequiredSkillModifier, ModifierDomain.CharId, 6, DogmaAttributeIds.DamageMultiplier, bonus, null, droneSkill));   // drone dmgMult += bonus%

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, [new DroneInput(drone, 1)]));

        // damageMultiplier 2.0 * (1 + (10 × 5) / 100) = 3.0; dps = 10 * 3.0 / (1000 / 1000) = 30
        Assert.Equal(30, result.Derived.DroneDps, 6);
    }

    [Fact]
    public void MissileDps_FromChargeDamageOverCycleTime_NoLauncherMultiplier()
    {
        const int launcher = 2410;
        const int ammo = 209;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(launcher, 509, 7, new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 10000))   // no damageMultiplier -> not a turret
            .Type(ammo, 386, 8, new SdeDogmaAttribute(117, 120));                                 // 120 kinetic damage

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(launcher, ModuleState.Active, ChargeTypeId: ammo)], SkillSource.AllLevelFive));

        Assert.Equal(12, result.Derived.MissileDps, 6);   // 120 / (10000 / 1000); no launcher damage multiplier
    }

    [Fact]
    public void LocationRequiredSkillModifier_NullSkill_ResolvesToCarrier_ReducingLauncherCycle()
    {
        // The missile-specialization RoF pattern (effect 1851 selfRof, patched with a null skillTypeID = carrier): the
        // skill reduces the cycle of launchers requiring it. rofBonus -2 * level 5 = -10% -> 100 / (10000 * 0.9 / 1000).
        const int launcher = 2410;
        const int ammo = 209;
        const int specialization = 20211;
        const int rofBonus = 293;
        var data = new FakeDogmaDataAccessor()
            .Attribute(DogmaAttributeIds.CycleTime, 0, stackable: false)
            .Attribute(rofBonus, 0, stackable: true)
            .Type(587, 25, 6)
            .Type(launcher, 509, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 10000),
                new SdeDogmaAttribute(182, specialization))                  // launcher requires the specialization
            .Type(ammo, 386, 8, new SdeDogmaAttribute(117, 100))
            .Type(specialization, 9000, 16, new SdeDogmaAttribute(rofBonus, -2))
            .TypeEffect(specialization, 163).TypeEffect(specialization, 1851)
            .Effect(163, 0, new ModifierInfo(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 0, rofBonus, DogmaAttributeIds.SkillLevel, null, null))               // rofBonus *= level
            .Effect(1851, 0, new ModifierInfo(ModifierFunc.LocationRequiredSkillModifier, ModifierDomain.ShipId, 6, DogmaAttributeIds.CycleTime, rofBonus, null, null)); // null skill -> carrier

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(launcher, ModuleState.Active, ChargeTypeId: ammo)], SkillSource.AllLevelFive));

        Assert.Equal(11.111, result.Derived.MissileDps, 3);
    }

    [Fact]
    public void Implant_ItemModifierShipId_BoostsShipAttribute()
    {
        // A navigation implant's real shape: ItemModifier on shipID, post-percent on maxVelocity from the implant's own
        // bonus attribute (1076 = 5, fixed — implants do not scale with skill level). 270 * (1 + 5/100) = 283.5.
        const int implant = 16003;
        const int bonusAttribute = 1076;
        var data = new FakeDogmaDataAccessor()
            .Attribute(DogmaAttributeIds.MaxVelocity, 0, stackable: true)
            .Type(587, 25, 6, new SdeDogmaAttribute(DogmaAttributeIds.MaxVelocity, 270))
            .Type(implant, 747, 20, new SdeDogmaAttribute(bonusAttribute, 5))            // category 20 = Implant
            .TypeEffect(implant, 2422)
            .Effect(2422, 0, new ModifierInfo(ModifierFunc.ItemModifier, ModifierDomain.ShipId, 6, DogmaAttributeIds.MaxVelocity, bonusAttribute, null, null));

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, Implants: [new ImplantInput(implant)]));

        Assert.Equal(283.5, result.Derived.MaxVelocity, 6);
    }

    [Fact]
    public void Implant_LocationRequiredSkillModifier_BoostsSkillRequiringModule()
    {
        // A surgical-strike implant: LocationRequiredSkillModifier on shipID, post-percent on the turret damage
        // multiplier of modules requiring the gunnery skill, from the implant's own bonus attribute (292 = 5, fixed).
        const int implant = 19687;
        const int turret = 2977;
        const int ammo = 21898;
        const int gunnery = 3300;
        const int bonus = 292;
        var data = new FakeDogmaDataAccessor()
            .Attribute(DogmaAttributeIds.DamageMultiplier, 1.0, stackable: false)
            .Type(587, 25, 6)
            .Type(turret, 74, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.DamageMultiplier, 2.0),
                new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 10000),
                new SdeDogmaAttribute(182, gunnery))                                     // turret requires gunnery
            .Type(ammo, 83, 8, new SdeDogmaAttribute(114, 10))
            .Type(implant, 742, 20, new SdeDogmaAttribute(bonus, 5))
            .TypeEffect(implant, 584)
            .Effect(584, 0, new ModifierInfo(ModifierFunc.LocationRequiredSkillModifier, ModifierDomain.ShipId, 6, DogmaAttributeIds.DamageMultiplier, bonus, null, gunnery));

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(turret, ModuleState.Active, ChargeTypeId: ammo)], SkillSource.AllLevelFive, Implants: [new ImplantInput(implant)]));

        // damageMultiplier 2.0 * (1 + 5/100) = 2.1; dps = 10 * 2.1 / (10000 / 1000) = 2.1
        Assert.Equal(2.1, result.Derived.TurretDps, 6);
    }

    [Fact]
    public void Overload_Category5SelfEffect_AppliesOnlyInOverloadState()
    {
        // A launcher's overload effect (category 5, real SDE shape): ItemModifier on itemID, post-percent on its own
        // cycle time from overloadRofBonus (-15). It must apply only when the module is overloaded.
        const int launcher = 2410;
        const int ammo = 209;
        const int overloadRofBonus = 1205;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(launcher, 509, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 10000),
                new SdeDogmaAttribute(overloadRofBonus, -15))                            // no damageMultiplier -> not a turret
            .Type(ammo, 386, 8, new SdeDogmaAttribute(117, 100))
            .TypeEffect(launcher, 3001)
            .Effect(3001, 5, new ModifierInfo(ModifierFunc.ItemModifier, ModifierDomain.ItemId, 6, DogmaAttributeIds.CycleTime, overloadRofBonus, null, null));   // category 5 = overload

        FitResult Run(ModuleState state) => Calculate(data, new FitInput(587,
            [new ModuleInput(launcher, state, ChargeTypeId: ammo)], SkillSource.AllLevelFive));

        Assert.Equal(10, Run(ModuleState.Active).Derived.MissileDps, 6);        // 100 / (10000 / 1000) — no overload bonus
        Assert.Equal(11.765, Run(ModuleState.Overload).Derived.MissileDps, 3);  // cycle * 0.85 -> 100 / 8.5
    }

    [Fact]
    public void Capacitor_SumsModuleLoadAndReportsStability()
    {
        // Ship cap 1000 GJ, rechargeRate 10000 ms -> peak recharge 2.5 * 1000 / 10 = 250 GJ/s. One active module
        // spending 50 GJ over a 1000 ms duration -> 50 GJ/s load, comfortably stable.
        const int module = 2024;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6,
                new SdeDogmaAttribute(DogmaAttributeIds.CapacitorCapacity, 1000),
                new SdeDogmaAttribute(DogmaAttributeIds.RechargeRate, 10000))
            .Type(module, 76, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.CapacitorNeed, 50),
                new SdeDogmaAttribute(DogmaAttributeIds.Duration, 1000));

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(module, ModuleState.Active)], SkillSource.AllLevelFive));
        var derived = result.Derived;

        Assert.Equal(1000, derived.CapacitorCapacity);
        Assert.Equal(250, derived.CapacitorRecharge, 6);
        Assert.Equal(50, derived.CapacitorUsed, 6);
        Assert.True(derived.CapacitorStable);
    }

    [Fact]
    public void Capacitor_Injector_AddsRechargeAndKeepsItStable()
    {
        // A cap booster (module 2024 with a charge carrying capacitorBonus 500, clip = capacity 40 / volume 10 = 4)
        // restores 500 GJ/s, covering a 400 GJ/s drain that would otherwise run the (regen-less) capacitor dry.
        const int booster = 2024, capCharge = 32006, drainModule = 5000;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6,
                new SdeDogmaAttribute(DogmaAttributeIds.CapacitorCapacity, 1000),
                new SdeDogmaAttribute(DogmaAttributeIds.RechargeRate, 1_000_000_000))   // negligible passive regen
            .Type(booster, 76, 7, new SdeDogmaAttribute(DogmaAttributeIds.Duration, 1000))
            .Type(capCharge, 87, 8, new SdeDogmaAttribute(DogmaAttributeIds.CapacitorBonus, 500))
            .Type(drainModule, 62, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.CapacitorNeed, 400),
                new SdeDogmaAttribute(DogmaAttributeIds.Duration, 1000))
            .Cargo(booster, capacity: 40, volume: 1)
            .Cargo(capCharge, capacity: 0, volume: 10);

        var result = Calculate(data, new FitInput(587,
        [
            new ModuleInput(booster, ModuleState.Active, ChargeTypeId: capCharge),
            new ModuleInput(drainModule, ModuleState.Active),
        ], SkillSource.AllLevelFive));
        var derived = result.Derived;

        Assert.Equal(400, derived.CapacitorUsed, 6);
        Assert.Equal(500, derived.CapacitorRecharge, 1);     // ~0 passive + 500 GJ/s injected
        Assert.True(derived.CapacitorStable);
    }

    [Fact]
    public void Capacitor_Nosferatu_AddsRechargeIncome()
    {
        // An energy nosferatu (recognised by its energyNosferatu effect) transfers 33 GJ every 5000 ms = 6.6 GJ/s into
        // our capacitor, on top of the peak recharge, with no activation cost — the reference oracle counts this self income too.
        const int nosferatu = 16507;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6,
                new SdeDogmaAttribute(DogmaAttributeIds.CapacitorCapacity, 1000),
                new SdeDogmaAttribute(DogmaAttributeIds.RechargeRate, 1_000_000_000))   // negligible passive regen
            .Type(nosferatu, 68, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.PowerTransferAmount, 33),
                new SdeDogmaAttribute(DogmaAttributeIds.Duration, 5000))
            .TypeEffect(nosferatu, 6197)
            .EffectNamed(6197, "energyNosferatuFalloff", 2);

        var result = Calculate(data, new FitInput(587, [new ModuleInput(nosferatu, ModuleState.Active)], SkillSource.AllLevelFive));

        Assert.Equal(6.6, result.Derived.CapacitorRecharge, 1);   // ~0 passive + 33 GJ / 5 s nosferatu income
    }

    [Fact]
    public void ReactiveArmorHardener_AdaptsArmorResonanceToTheDamageProfile()
    {
        // Armour resonances (EM/Th/Kin/Exp) from the other modules; a base RAH (shift 6%, 0.85 each) under uniform
        // damage settles to the reference profile (EM 1.0 / Th 0.775 / Kin 0.805 / Exp 0.82), leaving the ship at r0 x adapted.
        const int em = 267, thermal = 270, kinetic = 269, explosive = 268, shiftAmount = 1849, hardener = 4050;
        var data = new FakeDogmaDataAccessor()
            .Attribute(em, 1.0, stackable: false).Attribute(thermal, 1.0, stackable: false)
            .Attribute(kinetic, 1.0, stackable: false).Attribute(explosive, 1.0, stackable: false)
            .Attribute(shiftAmount, 0.0, stackable: true)
            .Type(587, 25, 6,
                new SdeDogmaAttribute(em, 0.3187), new SdeDogmaAttribute(thermal, 0.424),
                new SdeDogmaAttribute(kinetic, 0.424), new SdeDogmaAttribute(explosive, 0.4288))
            .Type(hardener, 1000, 7,
                new SdeDogmaAttribute(shiftAmount, 6),
                new SdeDogmaAttribute(em, 0.85), new SdeDogmaAttribute(thermal, 0.85),
                new SdeDogmaAttribute(kinetic, 0.85), new SdeDogmaAttribute(explosive, 0.85))
            .TypeEffect(hardener, 4928);

        var result = Calculate(data, new FitInput(587, [new ModuleInput(hardener, ModuleState.Active)], SkillSource.AllLevelFive));

        Assert.Equal(0.31870, result.ShipAttribute(em), 4);          // EM x 1.0
        Assert.Equal(0.32860, result.ShipAttribute(thermal), 4);     // Thermal x 0.775
        Assert.Equal(0.34132, result.ShipAttribute(kinetic), 4);     // Kinetic x 0.805
        Assert.Equal(0.351616, result.ShipAttribute(explosive), 5);  // Explosive x 0.82
    }

    [Fact]
    public void CharacterSnapshot_InjectsOnlyTrainedSkills()
    {
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6, new SdeDogmaAttribute(DogmaAttributeIds.CpuOutput, 100))
            .Type(3426, 9000, 16)
            .Type(3413, 9000, 16);

        var result = Calculate(data, new FitInput(587, [],
            SkillSource.From(new Dictionary<int, int> { [3426] = 4 })));

        // No skill effects seeded, so output is unchanged; the point is the snapshot path runs without all-V.
        Assert.Equal(100, result.Derived.CpuOutput);
    }

    [Fact]
    public void MiningYield_FromModuleMiningAmountOverDuration()
    {
        // A strip miner: miningAmount (77) m³ harvested each duration (73) cycle. yield/s = amount / (duration_ms / 1000).
        const int stripMiner = 17482;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(stripMiner, 464, 7,                                         // group 464 (Strip Miner), category 7 (Module)
                new SdeDogmaAttribute(DogmaAttributeIds.MiningAmount, 540),
                new SdeDogmaAttribute(DogmaAttributeIds.Duration, 180000));

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(stripMiner, ModuleState.Active)], SkillSource.AllLevelFive));

        Assert.Equal(3.0, result.Derived.MiningYield, 6);   // 540 / (180000 / 1000)
    }

    [Fact]
    public void MiningYield_OnlyCountsActiveMiners_ZeroWhenOnlinedOrOfflined()
    {
        // A miner only harvests while it is cycling (active); onlining or offlining it drops its yield to zero. The bug
        // the total summed every miner regardless of state, so disabling one left the yield unchanged.
        const int stripMiner = 17482;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(stripMiner, 464, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.MiningAmount, 540),
                new SdeDogmaAttribute(DogmaAttributeIds.Duration, 180000));

        double Yield(ModuleState state) => Calculate(data,
            new FitInput(587, [new ModuleInput(stripMiner, state)], SkillSource.AllLevelFive)).Derived.MiningYield;

        Assert.Equal(3.0, Yield(ModuleState.Active), 6);   // a running miner harvests
        Assert.Equal(0, Yield(ModuleState.Online), 6);     // onlined but not cycling -> nothing
        Assert.Equal(0, Yield(ModuleState.Passive), 6);    // offlined -> nothing
    }

    [Fact]
    public void MiningYield_DeploysMiningDrones_CappedByBandwidthAndFive()
    {
        // Mining drones (category 18) yield through the same miningAmount/duration attributes and deploy strongest-first
        // within the ship bandwidth — like combat drones. 10 Mbit/s ship, 5 Mbit/s each -> only 2 of the 5 in the bay fit.
        const int miningDrone = 10250;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6, new SdeDogmaAttribute(DogmaAttributeIds.DroneBandwidth, 10))
            .Type(miningDrone, 101, 18,                                       // group 101 (Mining Drone), category 18 (Drone)
                new SdeDogmaAttribute(DogmaAttributeIds.MiningAmount, 60),
                new SdeDogmaAttribute(DogmaAttributeIds.Duration, 60000),
                new SdeDogmaAttribute(DogmaAttributeIds.DroneBandwidthUse, 5));

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, [new DroneInput(miningDrone, 5)]));

        Assert.Equal(2.0, result.Derived.MiningYield, 6);   // 60 / 60s = 1.0 m³/s each, 2 deployed
    }

    [Fact]
    public void MiningYield_NonMiningFit_IsZero()
    {
        // A turret fit carries no miningAmount -> zero yield (the detail panel stays hidden).
        const int turret = 2977, ammo = 21898;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(turret, 74, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.DamageMultiplier, 2.0),
                new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 10000))
            .Type(ammo, 83, 8, new SdeDogmaAttribute(114, 10));

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(turret, ModuleState.Active, ChargeTypeId: ammo)], SkillSource.AllLevelFive));

        Assert.Equal(0, result.Derived.MiningYield);
    }

    [Fact]
    public void Contributions_Turret_SplitPerModuleAndSumToAggregate()
    {
        // Per-module breakdown: each turret's own DPS on the same resolve path as the aggregate, with the damage
        // split by type, summing back to the aggregate TurretDps.
        const int turret = 2977, ammo = 21898;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(turret, 74, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.DamageMultiplier, 2.0),
                new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 10000),
                new SdeDogmaAttribute(DogmaAttributeIds.MaxRange, 24000),
                new SdeDogmaAttribute(DogmaAttributeIds.Falloff, 8000),
                new SdeDogmaAttribute(DogmaAttributeIds.TrackingSpeed, 0.071))
            .Type(ammo, 83, 8,
                new SdeDogmaAttribute(114, 10), new SdeDogmaAttribute(116, 2),
                new SdeDogmaAttribute(117, 1), new SdeDogmaAttribute(118, 0));

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(turret, ModuleState.Active, ChargeTypeId: ammo),
             new ModuleInput(turret, ModuleState.Active, ChargeTypeId: ammo)], SkillSource.AllLevelFive));

        var turrets = result.Contributions.Where(c => c.Kind == ModuleContributionKind.Turret).ToList();
        Assert.Equal(2, turrets.Count);
        Assert.Equal(2.6, turrets[0].Dps, 6);                                  // (10+2+1+0) * 2.0 / 10
        Assert.Equal(20.0, turrets[0].DamageEm, 6);                            // 10 * 2.0 — per volley (in-game "Damage caused")
        Assert.Equal(2.0, turrets[0].DamageKinetic, 6);                        // 1 * 2.0 — per volley
        Assert.Equal(ammo, turrets[0].ChargeTypeId);
        Assert.Equal(24000, turrets[0].OptimalRange, 6);                       // engagement envelope
        Assert.Equal(8000, turrets[0].FalloffRange, 6);
        Assert.Equal(0.071, turrets[0].TrackingSpeed, 6);
        Assert.Equal(result.Derived.TurretDps, turrets.Sum(c => c.Dps), 6);    // per-module sums to the aggregate
    }

    [Fact]
    public void TurretDpsMax_EntropicDisintegrator_SpoolsToDamageMultiplierBonusMax()
    {
        // An entropic disintegrator (a turret carrying damageMultiplierBonusMax, attr 2734) ramps its damage up to
        // base * (1 + 2734). A T2 value of 2.125 gives a 3.125x fully-spooled max; the base (cycle-1) DPS is unchanged.
        const int disintegrator = 47271, ammo = 21898;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(disintegrator, 1986, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.DamageMultiplier, 2.0),
                new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 10000),
                new SdeDogmaAttribute(DogmaAttributeIds.DamageMultiplierBonusMax, 2.125))
            .Type(ammo, 83, 8,
                new SdeDogmaAttribute(114, 10), new SdeDogmaAttribute(116, 2),
                new SdeDogmaAttribute(117, 1), new SdeDogmaAttribute(118, 0));

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(disintegrator, ModuleState.Active, ChargeTypeId: ammo)], SkillSource.AllLevelFive));

        Assert.Equal(2.6, result.Derived.TurretDps, 6);          // base (cycle 1): 13 * 2.0 / 10
        Assert.Equal(8.125, result.Derived.TurretDpsMax, 6);     // fully spooled: 2.6 * (1 + 2.125)
        Assert.Equal(8.125, result.Derived.TotalDpsMax, 6);      // no drones/missiles
        var contribution = result.Contributions.Single(c => c.Kind == ModuleContributionKind.Turret);
        Assert.Equal(2.6, contribution.Dps, 6);
        Assert.Equal(8.125, contribution.DpsMax, 6);
    }

    [Fact]
    public void TurretDpsMax_NormalTurret_EqualsBaseDps()
    {
        // A turret without the ramp attr (2734) has DpsMax equal to its base DPS — no spool-up. The attribute has a 0.5
        // default, so a plain Resolve returns 0.5 and spuriously ramps every weapon by 1.5x (the live bug); registering
        // that default here makes the test red without the presence gate.
        const int turret = 2977, ammo = 21898;
        var data = new FakeDogmaDataAccessor()
            .Attribute(DogmaAttributeIds.DamageMultiplierBonusMax, 0.5, stackable: true)
            .Type(587, 25, 6)
            .Type(turret, 74, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.DamageMultiplier, 2.0),
                new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 10000))
            .Type(ammo, 83, 8,
                new SdeDogmaAttribute(114, 10), new SdeDogmaAttribute(116, 2),
                new SdeDogmaAttribute(117, 1), new SdeDogmaAttribute(118, 0));

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(turret, ModuleState.Active, ChargeTypeId: ammo)], SkillSource.AllLevelFive));

        Assert.Equal(result.Derived.TurretDps, result.Derived.TurretDpsMax, 6);
        var contribution = result.Contributions.Single(c => c.Kind == ModuleContributionKind.Turret);
        Assert.Equal(contribution.Dps, contribution.DpsMax, 6);
    }

    [Fact]
    public void TurretDpsSustained_AppliesTheReloadGap_ForAClippedWeapon()
    {
        // A clip of 10 shots (capacity 1.0 / charge volume 0.1) fires for 10*cycle, then reloads (10s); sustained DPS is
        // burst * clipDuration / (clipDuration + reload) = burst * 100000 / 110000.
        const int turret = 2977, ammo = 21898;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(turret, 74, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.DamageMultiplier, 2.0),
                new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 10000),
                new SdeDogmaAttribute(DogmaAttributeIds.ReloadTime, 10000))
            .Cargo(turret, 1.0, 0)
            .Type(ammo, 83, 8,
                new SdeDogmaAttribute(114, 10), new SdeDogmaAttribute(116, 2),
                new SdeDogmaAttribute(117, 1), new SdeDogmaAttribute(118, 0))
            .Cargo(ammo, 0, 0.1);

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(turret, ModuleState.Active, ChargeTypeId: ammo)], SkillSource.AllLevelFive));

        Assert.Equal(2.6, result.Derived.TurretDps, 6);
        Assert.Equal(2.6 * 10.0 / 11.0, result.Derived.TurretDpsSustained, 4);
        Assert.True(result.Derived.TurretDpsSustained < result.Derived.TurretDps);
        Assert.Equal(result.Derived.TurretDpsSustained, result.Derived.TotalDpsSustained, 6);   // no drones/missiles
    }

    [Fact]
    public void TurretDpsSustained_EqualsBurst_WithoutAClip()
    {
        // No capacity/volume → clip resolves to 0 (effectively infinite, as a laser's permanent crystal), so even with a
        // reload attr present the sustained rate equals the burst — proves the no-reload path.
        const int turret = 2977, ammo = 21898;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(turret, 74, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.DamageMultiplier, 2.0),
                new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 10000),
                new SdeDogmaAttribute(DogmaAttributeIds.ReloadTime, 10000))
            .Type(ammo, 83, 8,
                new SdeDogmaAttribute(114, 10), new SdeDogmaAttribute(116, 2),
                new SdeDogmaAttribute(117, 1), new SdeDogmaAttribute(118, 0));

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(turret, ModuleState.Active, ChargeTypeId: ammo)], SkillSource.AllLevelFive));

        Assert.Equal(result.Derived.TurretDps, result.Derived.TurretDpsSustained, 6);
    }

    [Fact]
    public void Contributions_Drone_ReportsPerDroneDps()
    {
        // A drone contribution is the single-drone DPS (what a per-drone tooltip shows), not the count-scaled aggregate.
        const int drone = 2185;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(drone, 100, 18,
                new SdeDogmaAttribute(118, 32),
                new SdeDogmaAttribute(DogmaAttributeIds.DamageMultiplier, 1.92),
                new SdeDogmaAttribute(DogmaAttributeIds.CycleTime, 4000));

        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, [new DroneInput(drone, 5)]));

        var contribution = Assert.Single(result.Contributions);
        Assert.Equal(ModuleContributionKind.Drone, contribution.Kind);
        Assert.True(contribution.IsDrone);
        Assert.Equal(15.36, contribution.Dps, 6);            // 32 * 1.92 / 4 — per drone, not × 5
        Assert.Equal(61.44, contribution.DamageThermal, 6);  // 32 * 1.92 — per volley
        Assert.Equal(76.8, result.Derived.DroneDps, 6);      // the aggregate still scales by the count
    }

    [Fact]
    public void Contributions_MiningModule_ReportsYieldAndCycleVolume()
    {
        const int stripMiner = 17482;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(stripMiner, 464, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.MiningAmount, 540),
                new SdeDogmaAttribute(DogmaAttributeIds.Duration, 180000));

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(stripMiner, ModuleState.Active)], SkillSource.AllLevelFive));

        var contribution = Assert.Single(result.Contributions);
        Assert.Equal(ModuleContributionKind.Mining, contribution.Kind);
        Assert.Equal(3.0, contribution.MiningYieldPerSec, 6);                  // 540 / 180s
        Assert.Equal(540, contribution.M3PerCycle, 6);
        Assert.Equal(result.Derived.MiningYield, contribution.MiningYieldPerSec, 6);
    }

    [Fact]
    public void Contributions_LocalArmorRepairer_ReportsRepPerSecondAndLayer()
    {
        // A local armor repairer restores armor each duration cycle: rep/s = amount / (duration/1000). The gate
        // is "no optimal range" — a remote repairer carries maxRange and is excluded from the local-repair readout.
        const int repairer = 3530;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(repairer, 80, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.ArmorRepairAmount, 80),
                new SdeDogmaAttribute(DogmaAttributeIds.Duration, 8000));

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(repairer, ModuleState.Active)], SkillSource.AllLevelFive));

        var contribution = Assert.Single(result.Contributions);
        Assert.Equal(ModuleContributionKind.LocalRepair, contribution.Kind);
        Assert.Equal(RepairLayer.Armor, contribution.RepairLayer);
        Assert.Equal(10.0, contribution.RepPerSec, 6);                         // 80 / 8s
    }

    [Fact]
    public void Contributions_CapacitorBooster_ReportsCapPerSecond()
    {
        // A cap booster's loaded charge restores capacitor each cycle: cap/s = capacitorBonus / (cycle/1000).
        const int booster = 4258, charge = 263;
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6)
            .Type(booster, 100, 7,
                new SdeDogmaAttribute(DogmaAttributeIds.Duration, 10000))
            .Type(charge, 1, 8,
                new SdeDogmaAttribute(DogmaAttributeIds.CapacitorBonus, 200));

        var result = Calculate(data, new FitInput(587,
            [new ModuleInput(booster, ModuleState.Active, ChargeTypeId: charge)], SkillSource.AllLevelFive));

        var contribution = Assert.Single(result.Contributions);
        Assert.Equal(ModuleContributionKind.Capacitor, contribution.Kind);
        Assert.Equal(20.0, contribution.CapPerSec, 6);                         // 200 / 10s (no reactivation delay)
    }

    [Fact]
    public void Calculate_Ehp_UniformProfile_MatchesWeightedFormula()
    {
        // Parity guarantee: DamageProfile.Uniform (0.25×4) must produce exactly the same EHP as the old
        // arithmetic-mean formula (sum / 4). For resonances [1.0, 1.0, 0.5, 0.5]:
        //   old:  1000 / ((1.0 + 1.0 + 0.5 + 0.5) / 4) = 1000 / 0.75 = 1333.33…
        //   new:  1000 / (0.25*1.0 + 0.25*1.0 + 0.25*0.5 + 0.25*0.5) = 1000 / 0.75 = 1333.33…
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6,
                new SdeDogmaAttribute(DogmaAttributeIds.ShieldCapacity, 1000),
                new SdeDogmaAttribute(271, 1.0), new SdeDogmaAttribute(274, 1.0),
                new SdeDogmaAttribute(273, 0.5), new SdeDogmaAttribute(272, 0.5));

        var withUniform  = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, Profile: DamageProfile.Uniform));
        var withoutProfile = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive));

        Assert.Equal(withoutProfile.Derived.ShieldEhp, withUniform.Derived.ShieldEhp, 10);
        Assert.Equal(1333.33, withUniform.Derived.ShieldEhp, 2);
    }

    [Fact]
    public void Calculate_Ehp_WeightedProfile_Guristas()
    {
        // Guristas NPC profile: Kinetic 54 %, Thermal 46 %.
        // Shield resonances: EM=0.0 (no EM resist on ship), Th=0.80, Kin=0.60, Exp=0.0.
        //   weighted resonance = 0.54*0.60 + 0.46*0.80 = 0.324 + 0.368 = 0.692
        //   ShieldEhp = 1000 / 0.692 ≈ 1445.09
        var data = new FakeDogmaDataAccessor()
            .Type(587, 25, 6,
                new SdeDogmaAttribute(DogmaAttributeIds.ShieldCapacity, 1000),
                new SdeDogmaAttribute(271, 1.0),   // EM resonance (no resist)
                new SdeDogmaAttribute(274, 0.80),  // Thermal
                new SdeDogmaAttribute(273, 0.60),  // Kinetic
                new SdeDogmaAttribute(272, 1.0));  // Explosive resonance (no resist)

        var profile = new DamageProfile(Em: 0, Th: 0.46, Kin: 0.54, Exp: 0).Normalized();
        var result = Calculate(data, new FitInput(587, [], SkillSource.AllLevelFive, Profile: profile));

        Assert.Equal(1445.09, result.Derived.ShieldEhp, 2);
    }
}
