using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// Pass 4: the derived stats from the evaluated graph. Fitting resources are summed from the
/// modules / read off the ship and rounded to two decimals; EHP is each layer's hit points divided by the weighted
/// sum of resonances for the given <see cref="DamageProfile"/>. Max velocity and signature radius come
/// straight off the resolved ship — the propulsion-module boost and the microwarpdrive signature penalty are folded
/// in data-driven through the velocityBoost patch (DogmaPatches). All values come through the evaluator so they
/// reflect skills + modules.
/// </summary>
public sealed class DerivedStatsCalculator(DogmaEvaluator evaluator, IDogmaDataAccessor data) : ISingletonService
{
    // inventory category 18 = Drone; drones carry their damage attributes directly (no charge).
    private const int DroneCategoryId = 18;

    public DerivedStats Calculate(DogmaFit fit, DamageProfile? profile = null)
    {
        var p = profile ?? DamageProfile.Uniform;

        // CPU/PG used: the ship's cpuLoad/powerLoad, filled by each online module's cpuPowerLoad patch (DogmaPatches).
        // Reading the load rather than summing the modules means an offline module no longer consumes fitting resources.
        var cpuUsed = Round2(evaluator.Resolve(fit.Ship, DogmaAttributeIds.CpuLoad));
        var powerUsed = Round2(evaluator.Resolve(fit.Ship, DogmaAttributeIds.PowerLoad));
        var cpuOutput = Round2(evaluator.Resolve(fit.Ship, DogmaAttributeIds.CpuOutput));
        var powerOutput = Round2(evaluator.Resolve(fit.Ship, DogmaAttributeIds.PowerOutput));
        // Max velocity: the resolved attribute (skills, nano/overdrive, …) times each active propulsion module's boost.
        // The MWD signature penalty stays data-driven (DogmaPatches), so signatureRadius is just the resolved attribute.
        var maxVelocity = Propulsion(fit, evaluator.Resolve(fit.Ship, DogmaAttributeIds.MaxVelocity));
        var signatureRadius = evaluator.Resolve(fit.Ship, DogmaAttributeIds.SignatureRadius);
        // Align time is the synthetic alignTime attribute (DogmaPatches): -ln(0.25) * agility * mass / 1e6.
        var alignTime = evaluator.Resolve(fit.Ship, DogmaAttributeIds.AlignTime);

        var shieldEhp = LayerEhp(fit.Ship, DogmaAttributeIds.ShieldCapacity, DogmaAttributeIds.ShieldResonance, p);
        var armorEhp = LayerEhp(fit.Ship, DogmaAttributeIds.ArmorHp, DogmaAttributeIds.ArmorResonance, p);
        var structureEhp = LayerEhp(fit.Ship, DogmaAttributeIds.StructureHp, DogmaAttributeIds.StructureResonance, p);

        var cap = Capacitor(fit);
        var drones = Drones(fit);
        var remote = RemoteAssistance(fit);

        return new DerivedStats(cpuUsed, cpuOutput, powerUsed, powerOutput, maxVelocity, signatureRadius, alignTime,
            shieldEhp, armorEhp, structureEhp, shieldEhp + armorEhp + structureEhp,
            TurretDps(fit), drones.Dps, MissileDps(fit),
            cap.Capacity, cap.Recharge, cap.Used, cap.Stable, cap.StablePercent, cap.DepletesInSeconds,
            drones.BandwidthUsed, drones.ActiveCount, MiningYield(fit),
            RemoteArmorRepPerSec: remote.ArmorRepPerSec,
            RemoteShieldRepPerSec: remote.ShieldRepPerSec,
            RemoteHullRepPerSec: remote.HullRepPerSec,
            RemoteCapPerSec: remote.CapPerSec,
            RemoteArmorRangeMeters: remote.ArmorRangeMeters,
            RemoteShieldRangeMeters: remote.ShieldRangeMeters,
            RemoteHullRangeMeters: remote.HullRangeMeters,
            RemoteCapRangeMeters: remote.CapRangeMeters,
            TurretDpsMax: TurretDpsMax(fit),
            TurretDpsSustained: TurretDpsSustained(fit),
            MissileDpsSustained: MissileDpsSustained(fit),
            FighterDps: FighterDps(fit),
            FighterDpsSustained: FighterDpsSustained(fit));
    }

    /// <summary>Each fitted module's and drone's own resolved contribution, computed on the same resolve paths
    /// as the aggregate stats. Modules come first in fit order (index-aligned with the slot list), then the drones.
    /// The per-module tooltip (a later UI pass) reads from this; the aggregate <see cref="DerivedStats"/> is unchanged.</summary>
    public IReadOnlyList<ModuleContribution> CalculateContributions(DogmaFit fit)
    {
        var shipMass = evaluator.Resolve(fit.Ship, DogmaAttributeIds.Mass);
        var contributions = new List<ModuleContribution>(fit.Modules.Count + fit.Drones.Count);
        foreach (var module in fit.Modules)
            contributions.Add(_ModuleContribution(module, shipMass));
        foreach (var drone in fit.Drones)
            if (drone.CategoryId == DroneCategoryId)
                contributions.Add(_DroneContribution(drone));
        return contributions;
    }

    private ModuleContribution _ModuleContribution(DogmaItem module, double shipMass)
    {
        var cycleTime = evaluator.Resolve(module, DogmaAttributeIds.CycleTime);

        if (module.Charge is { } charge && IsTurret(module))
        {
            var damage = _DamageOverCycle(charge, evaluator.Resolve(module, DogmaAttributeIds.DamageMultiplier), cycleTime);
            return new ModuleContribution(module.TypeId, ModuleContributionKind.Turret, module.State,
                ChargeTypeId: charge.TypeId, Dps: damage.Dps, DamageEm: damage.Em, DamageThermal: damage.Thermal,
                DamageKinetic: damage.Kinetic, DamageExplosive: damage.Explosive,
                OptimalRange: evaluator.Resolve(module, DogmaAttributeIds.MaxRange),
                FalloffRange: evaluator.Resolve(module, DogmaAttributeIds.Falloff),
                TrackingSpeed: evaluator.Resolve(module, DogmaAttributeIds.TrackingSpeed),
                DpsMax: damage.Dps * (1 + RampBonus(module)));
        }

        if (module.Charge is { } missileCharge)
        {
            var damage = _DamageOverCycle(missileCharge, 1, cycleTime);
            if (damage.Dps > 0)
            {
                // A missile's range is its velocity carried over its flight time (both fold in skill/ship bonuses), unlike
                // a turret's optimal/falloff. Surfaced on OptimalRange so the tooltip can show a single range line.
                var velocity = evaluator.Resolve(missileCharge, DogmaAttributeIds.MaxVelocity);
                var flightTime = evaluator.Resolve(missileCharge, DogmaAttributeIds.ExplosionDelay);   // ms
                return new ModuleContribution(module.TypeId, ModuleContributionKind.Missile, module.State,
                    ChargeTypeId: missileCharge.TypeId, Dps: damage.Dps, DamageEm: damage.Em, DamageThermal: damage.Thermal,
                    DamageKinetic: damage.Kinetic, DamageExplosive: damage.Explosive,
                    OptimalRange: velocity * flightTime / 1000.0);
            }
        }

        var miningYield = UnitMiningYield(module);
        if (miningYield > 0)
            return new ModuleContribution(module.TypeId, ModuleContributionKind.Mining, module.State,
                ChargeTypeId: module.Charge?.TypeId, MiningYieldPerSec: miningYield,
                M3PerCycle: evaluator.Resolve(module, DogmaAttributeIds.MiningAmount),
                OptimalRange: evaluator.Resolve(module, DogmaAttributeIds.MaxRange));

        if (module.State >= ModuleState.Active)
        {
            var boost = _PropulsionBoostPercent(module, shipMass);
            if (boost > 0)
                return new ModuleContribution(module.TypeId, ModuleContributionKind.Propulsion, module.State, SpeedBoostPercent: boost);
        }

        if (_LocalRepair(module) is { } repair)
            return repair;

        if (_RemoteRepair(module) is { } remoteRepair)
            return remoteRepair;

        if (_RemoteCapTransfer(module) is { } remoteCapTransfer)
            return remoteCapTransfer;

        if (_CapacitorContribution(module) is { } capacitor)
            return capacitor;

        return new ModuleContribution(module.TypeId, ModuleContributionKind.None, module.State, ChargeTypeId: module.Charge?.TypeId);
    }

    private ModuleContribution _DroneContribution(DogmaItem drone)
    {
        var miningYield = UnitMiningYield(drone);
        if (miningYield > 0)
            return new ModuleContribution(drone.TypeId, ModuleContributionKind.Mining, drone.State, IsDrone: true,
                MiningYieldPerSec: miningYield, M3PerCycle: evaluator.Resolve(drone, DogmaAttributeIds.MiningAmount),
                OptimalRange: evaluator.Resolve(drone, DogmaAttributeIds.MaxRange));

        // Logistic drones (group 640) project remote reps/cap — same attrs as remote modules. Flag as drone.
        if (_RemoteRepair(drone) is { } droneRemoteRep)
            return droneRemoteRep with { IsDrone = true };
        if (_RemoteCapTransfer(drone) is { } droneRemoteCap)
            return droneRemoteCap with { IsDrone = true };

        var damage = _DamageOverCycle(drone, evaluator.Resolve(drone, DogmaAttributeIds.DamageMultiplier),
            evaluator.Resolve(drone, DogmaAttributeIds.CycleTime));
        return new ModuleContribution(drone.TypeId, ModuleContributionKind.Drone, drone.State, IsDrone: true,
            Dps: damage.Dps, DamageEm: damage.Em, DamageThermal: damage.Thermal, DamageKinetic: damage.Kinetic,
            DamageExplosive: damage.Explosive,
            OptimalRange: evaluator.Resolve(drone, DogmaAttributeIds.MaxRange),
            FalloffRange: evaluator.Resolve(drone, DogmaAttributeIds.Falloff),
            TrackingSpeed: evaluator.Resolve(drone, DogmaAttributeIds.TrackingSpeed));
    }

    // A local repair module restores one layer of our own ship each duration cycle (ref tooltips/08). Gate: a positive
    // repair amount and no optimal range — a remote repairer projects onto a target (maxRange > 0) and is excluded.
    //   rep/s = repaired-per-cycle / (duration_ms / 1000)
    private ModuleContribution? _LocalRepair(DogmaItem module)
    {
        if (evaluator.Resolve(module, DogmaAttributeIds.MaxRange) > 0)
            return null;   // remote repairer projects onto a target, not ourselves
        var duration = evaluator.Resolve(module, DogmaAttributeIds.Duration);
        if (duration <= 0)
            return null;

        var (amount, layer) = _RepairAmount(module);
        return amount > 0
            ? new ModuleContribution(module.TypeId, ModuleContributionKind.LocalRepair, module.State,
                RepPerSec: amount / (duration / 1000.0), RepairLayer: layer)
            : null;
    }

    // The repaired layer and its per-cycle amount: shield boosters, armor and hull repairers each carry their own
    // amount attribute. Checked shield → armor → hull (a module restores one layer).
    private (double Amount, RepairLayer Layer) _RepairAmount(DogmaItem module)
    {
        var shield = evaluator.Resolve(module, DogmaAttributeIds.ShieldBoostAmount);
        if (shield > 0)
            return (shield, RepairLayer.Shield);
        var armor = evaluator.Resolve(module, DogmaAttributeIds.ArmorRepairAmount);
        if (armor > 0)
            return (armor, RepairLayer.Armor);
        var hull = evaluator.Resolve(module, DogmaAttributeIds.HullRepairAmount);
        return hull > 0 ? (hull, RepairLayer.Hull) : (0, RepairLayer.None);
    }

    // A remote repair module projects a repair onto a target each duration cycle. Gate: maxRange > 0 (the
    // module has a projection range) AND a positive repair amount. Mirror of _LocalRepair but inverted gate.
    //   rep/s = repaired-per-cycle / (duration_ms / 1000)
    private ModuleContribution? _RemoteRepair(DogmaItem module)
    {
        var range = evaluator.Resolve(module, DogmaAttributeIds.MaxRange);
        if (range <= 0)
            return null;   // no projection range — not a remote repairer
        var duration = evaluator.Resolve(module, DogmaAttributeIds.Duration);
        if (duration <= 0)
            return null;

        var (amount, layer) = _RepairAmount(module);
        return amount > 0
            ? new ModuleContribution(module.TypeId, ModuleContributionKind.RemoteRepair, module.State,
                RepPerSec: amount / (duration / 1000.0), RepairLayer: layer, RemoteRangeMeters: range)
            : null;
    }

    // A remote capacitor transmitter gives GJ to a target each duration cycle. Gate: maxRange > 0 AND a
    // positive powerTransferAmount. Distinct from a nosferatu (self-income) and a neutraliser (drain only).
    //   cap/s = transferred-per-cycle / (duration_ms / 1000)
    private ModuleContribution? _RemoteCapTransfer(DogmaItem module)
    {
        var range = evaluator.Resolve(module, DogmaAttributeIds.MaxRange);
        if (range <= 0)
            return null;
        var duration = evaluator.Resolve(module, DogmaAttributeIds.Duration);
        if (duration <= 0)
            return null;

        var transfer = evaluator.Resolve(module, DogmaAttributeIds.PowerTransferAmount);
        return transfer > 0
            ? new ModuleContribution(module.TypeId, ModuleContributionKind.RemoteCapTransfer, module.State,
                CapPerSec: transfer / (duration / 1000.0), RemoteRangeMeters: range)
            : null;
    }

    // A capacitor source feeding our own capacitor: an energy nosferatu (transfers a target's cap into ours) or a cap
    // booster (a loaded charge restoring cap). Remote cap transmitters give a target cap, not us, so they are excluded.
    //   cap/s = transferred-or-restored-per-cycle / (cycle_ms / 1000)
    private ModuleContribution? _CapacitorContribution(DogmaItem module)
    {
        if (IsNosferatu(module))
        {
            var cycle = Math.Max(evaluator.Resolve(module, DogmaAttributeIds.CycleTime),
                                 evaluator.Resolve(module, DogmaAttributeIds.Duration));
            var transfer = evaluator.Resolve(module, DogmaAttributeIds.PowerTransferAmount);
            if (transfer > 0 && cycle > 0)
                return new ModuleContribution(module.TypeId, ModuleContributionKind.Capacitor, module.State,
                    CapPerSec: transfer / (cycle / 1000.0));
        }

        if (module.Charge is { } charge && evaluator.Resolve(charge, DogmaAttributeIds.CapacitorBonus) is > 0 and var bonus)
        {
            var cycle = evaluator.Resolve(module, DogmaAttributeIds.Duration)
                + evaluator.Resolve(module, DogmaAttributeIds.ReactivationDelay);
            if (cycle > 0)
                return new ModuleContribution(module.TypeId, ModuleContributionKind.Capacitor, module.State,
                    ChargeTypeId: charge.TypeId, CapPerSec: bonus / (cycle / 1000.0));
        }

        return null;
    }

    // Propulsion speed (afterburner/microwarpdrive): each active prop module boosts maxVelocity by
    // speedFactor*speedBoostFactor/shipMass (a percentage), stacking-penalised across modules — the standard per-module
    // formula. A code aggregate rather than a data-driven modifier: the boost divides a per-module value by ship mass,
    // which the shared-attribute modifier approach cannot do without compounding wrongly across multiple prop modules
    // . mass additions and the MWD signature penalty stay data-driven (DogmaPatches).
    private double Propulsion(DogmaFit fit, double baseVelocity)
    {
        var shipMass = evaluator.Resolve(fit.Ship, DogmaAttributeIds.Mass);
        if (shipMass <= 0)
            return baseVelocity;

        var boosts = new List<double>();
        foreach (var module in fit.Modules)
        {
            if (module.State < ModuleState.Active)
                continue;
            var boost = _PropulsionBoostPercent(module, shipMass);   // a percentage on maxVelocity
            if (boost > 0)
                boosts.Add(boost);
        }

        boosts.Sort((left, right) => Math.Abs(right).CompareTo(Math.Abs(left)));
        var velocity = baseVelocity;
        for (var rank = 0; rank < boosts.Count; rank++)
            velocity *= 1 + boosts[rank] / 100.0 * DogmaPenalty.StackingMultiplier(rank);
        return velocity;
    }

    // One propulsion module's pre-stacking speed boost as a percentage of maxVelocity: speedFactor * speedBoostFactor
    // / shipMass. Shared by the velocity aggregate and the per-module contribution so the formula lives in one place.
    private double _PropulsionBoostPercent(DogmaItem module, double shipMass)
    {
        if (shipMass <= 0)
            return 0;
        var speedBoostFactor = evaluator.Resolve(module, DogmaAttributeIds.SpeedBoostFactor);
        if (speedBoostFactor <= 0)
            return 0;
        return evaluator.Resolve(module, DogmaAttributeIds.SpeedFactor) * speedBoostFactor / shipMass;
    }

    // Capacitor: peak recharge vs the active modules' load, then a discrete-event simulation for stability. Peak
    // recharge is 2.5 * capacity / rechargeRate(s) (EVE's recharge curve peaks at 25% cap); a module drains its
    // capacitorNeed every activation cycle (the larger of speed/duration, plus reactivation delay). Injectors (a later
    // step) restore cap. The simulator decides stable% vs depletes-in.
    private (double Capacity, double Recharge, double Used, bool Stable, double StablePercent, double DepletesInSeconds)
        Capacitor(DogmaFit fit)
    {
        var capacity = evaluator.Resolve(fit.Ship, DogmaAttributeIds.CapacitorCapacity);
        var rechargeRate = evaluator.Resolve(fit.Ship, DogmaAttributeIds.RechargeRate);
        var peakRecharge = rechargeRate > 0 ? 2.5 * capacity / (rechargeRate / 1000.0) : 0;

        var drains = new List<CapDrain>();
        double used = 0;
        double added = 0;
        foreach (var module in fit.Modules)
        {
            if (module.State < ModuleState.Active)
                continue;

            // Cap booster: a loaded charge with a capacitorBonus restores cap (a negative drain). It reloads after its
            // charge clip (module capacity / charge volume), so the fill is intermittent.
            if (module.Charge is { } charge && evaluator.Resolve(charge, DogmaAttributeIds.CapacitorBonus) is > 0 and var bonus)
            {
                var injectorCycle = evaluator.Resolve(module, DogmaAttributeIds.Duration)
                    + evaluator.Resolve(module, DogmaAttributeIds.ReactivationDelay);
                if (injectorCycle <= 0)
                    continue;
                var clip = ClipSize(module.TypeId, charge.TypeId);
                var reload = evaluator.Resolve(module, DogmaAttributeIds.ReloadTime);
                drains.Add(new CapDrain(injectorCycle, -bonus, clip, DisableStagger: false, reload, IsInjector: true));
                var averageCycle = clip > 0 ? injectorCycle + reload / clip : injectorCycle;
                added += bonus / (averageCycle / 1000.0);
                continue;
            }

            // Energy nosferatu: drains a target and adds the transfer to our own capacitor. We assume it runs (the
            // standard headless assumption), so the net income is a continuous cap source — added to the reported recharge
            // and to the stability sim as a negative drain. Distinguished from a neutraliser (no self income) by its
            // energyNosferatu effect; a nosferatu has no activation cost, so the transfer is its net gain.
            if (IsNosferatu(module))
            {
                var transfer = evaluator.Resolve(module, DogmaAttributeIds.PowerTransferAmount);
                var nosCycle = Math.Max(evaluator.Resolve(module, DogmaAttributeIds.CycleTime),
                                        evaluator.Resolve(module, DogmaAttributeIds.Duration));
                if (transfer > 0 && nosCycle > 0)
                {
                    drains.Add(new CapDrain(nosCycle, -transfer, ClipSize: 0, DisableStagger: false, ReloadTime: 0, IsInjector: false));
                    added += transfer / (nosCycle / 1000.0);
                }
                continue;
            }

            var capNeed = evaluator.Resolve(module, DogmaAttributeIds.CapacitorNeed);
            if (capNeed == 0)
                continue;
            var cycle = Math.Max(evaluator.Resolve(module, DogmaAttributeIds.CycleTime),
                                 evaluator.Resolve(module, DogmaAttributeIds.Duration));
            var fullCycle = cycle + evaluator.Resolve(module, DogmaAttributeIds.ReactivationDelay);
            if (fullCycle <= 0)
                continue;
            drains.Add(new CapDrain(fullCycle, capNeed, ClipSize: 0, DisableStagger: IsTurret(module), ReloadTime: 0, IsInjector: false));
            if (capNeed > 0)
                used += capNeed / (fullCycle / 1000.0);
        }

        var simulation = new CapacitorSimulator(capacity, rechargeRate).Run(drains);
        return (capacity, peakRecharge + added, used, simulation.Stable, simulation.StablePercent, simulation.DepletesInSeconds);
    }

    // A cap booster's charge clip: how many charges fit before a reload (module cargo capacity / charge volume).
    private int ClipSize(int moduleTypeId, int chargeTypeId)
    {
        var capacity = data.GetCapacity(moduleTypeId) ?? 0;
        var volume = data.GetVolume(chargeTypeId) ?? 0;
        return volume > 0 ? (int)Math.Floor(capacity / volume) : 0;
    }

    // Missile DPS: a launcher fires its loaded missile, whose damage sits on the charge (boosted by missile skills /
    // ship bonuses through the pipeline — Warhead Upgrades' real modifierInfo plus the patched size-skill bonuses) with
    // no launcher damage multiplier, unlike a turret.
    //   dps = sum(charge em/explosive/kinetic/thermal) / (cycleTime_ms / 1000), summed over the launchers.
    // Gate: a charged module that is not a turret and whose charge actually deals damage (excludes probes/miners).
    private double MissileDps(DogmaFit fit)
    {
        double total = 0;
        foreach (var module in fit.Modules)
        {
            if (module.State < ModuleState.Active || module.Charge is not { } charge || IsTurret(module))
                continue;
            // A launcher has no damage multiplier (unlike a turret) — the charge's damage is the volley.
            total += _DamageOverCycle(charge, 1, evaluator.Resolve(module, DogmaAttributeIds.CycleTime)).Dps;
        }
        return total;
    }

    // Sustained (reload-adjusted) turret DPS: a clip of N shots fires for N*cycle, then the weapon reloads (attr 1795)
    // before the next clip, so the long-run rate is burst * clipDuration / (clipDuration + reload). Mirrors TurretDps.
    private double TurretDpsSustained(DogmaFit fit)
    {
        double total = 0;
        foreach (var module in fit.Modules)
        {
            if (module.State < ModuleState.Active || module.Charge is not { } charge || !IsTurret(module))
                continue;
            var burst = _DamageOverCycle(charge, evaluator.Resolve(module, DogmaAttributeIds.DamageMultiplier),
                evaluator.Resolve(module, DogmaAttributeIds.CycleTime)).Dps;
            total += _ReloadAdjusted(module, burst);
        }
        return total;
    }

    // Sustained (reload-adjusted) missile DPS, mirroring MissileDps.
    private double MissileDpsSustained(DogmaFit fit)
    {
        double total = 0;
        foreach (var module in fit.Modules)
        {
            if (module.State < ModuleState.Active || module.Charge is not { } charge || IsTurret(module))
                continue;
            total += _ReloadAdjusted(module, _DamageOverCycle(charge, 1, evaluator.Resolve(module, DogmaAttributeIds.CycleTime)).Dps);
        }
        return total;
    }

    // Reload-adjusted sustained DPS for one weapon: burst * clipDuration / (clipDuration + reload). A weapon without a
    // clip or a reload — lasers (reload ~0 ms), drones, anything uncharged — sustains its burst (clip resolves to 0 =
    // effectively infinite). The cycle-sequence average time = (N*cycle + reload)/N; only the denominator scales.
    private double _ReloadAdjusted(DogmaItem module, double burstDps)
    {
        if (module.Charge is not { } charge || burstDps <= 0)
            return burstDps;
        var clip = ClipSize(module.TypeId, charge.TypeId);
        if (clip <= 0)
            return burstDps;                                                          // infinite clip → no reload gap
        var reload = evaluator.Resolve(module, DogmaAttributeIds.ReloadTime);
        if (reload <= 0)
            return burstDps;                                                          // lasers reload in ~0.01 ms
        var clipDuration = clip * evaluator.Resolve(module, DogmaAttributeIds.CycleTime);
        return clipDuration > 0 ? burstDps * clipDuration / (clipDuration + reload) : burstDps;
    }

    // Per-second damage of a damage source (a loaded charge or a drone), scaled by a weapon multiplier over its cycle
    // and split by type. The single per-item DPS formula behind both the turret/missile/drone aggregates and the
    // per-module contribution — no second formula. The aggregate Dps is computed exactly as before (sum then
    // scale) so the regression numbers do not shift; the per-type split is for the contribution readout only.
    private (double Dps, double Em, double Thermal, double Kinetic, double Explosive) _DamageOverCycle(
        DogmaItem damageSource, double multiplier, double cycleMs)
    {
        if (cycleMs <= 0)
            return default;
        var em = evaluator.Resolve(damageSource, DogmaAttributeIds.EmDamage);
        var explosive = evaluator.Resolve(damageSource, DogmaAttributeIds.ExplosiveDamage);
        var kinetic = evaluator.Resolve(damageSource, DogmaAttributeIds.KineticDamage);
        var thermal = evaluator.Resolve(damageSource, DogmaAttributeIds.ThermalDamage);
        var dps = (em + explosive + kinetic + thermal) * multiplier / (cycleMs / 1000.0);
        // Per-volley damage per type (the in-game "Damage caused" line): the charge's damage scaled by the weapon
        // multiplier, not divided by the cycle. Dps stays the per-second total.
        return (dps, em * multiplier, thermal * multiplier, kinetic * multiplier, explosive * multiplier);
    }

    // Fighter squadron DPS: each launched squadron's per-fighter ability damage times its active fighter count
    // (DogmaItem.Quantity). Fighters carry their damage on per-ability attributes (the primary attack ability + the heavy
    // fighter's secondary missile salvo), so this reads those rather than the universal 114/116/117/118 — the same formula
    // shape as _DamageOverCycle over a different attribute set (EVE stores fighter damage per-ability). Support and
    // superiority squadrons carry no attack multiplier and contribute 0. Every attr resolves through the evaluator, so the
    // Fighters skill (+5%/lvl) and the hull fighter-damage bonuses fold in (fighters are char-owned, see DogmaEffectCollector).
    //   squadronDps = (attackAbilityDps + secondarySalvoDps) * activeFighterCount
    private double FighterDps(DogmaFit fit)
    {
        double total = 0;
        foreach (var fighter in fit.Fighters)
        {
            var perFighter =
                _FighterAbilityDps(fighter, DogmaAttributeIds.FighterAttackDamageMultiplier, DogmaAttributeIds.FighterAttackEm,
                    DogmaAttributeIds.FighterAttackThermal, DogmaAttributeIds.FighterAttackKinetic,
                    DogmaAttributeIds.FighterAttackExplosive, DogmaAttributeIds.FighterAttackDuration)
                + _FighterAbilityDps(fighter, DogmaAttributeIds.FighterMissilesDamageMultiplier, DogmaAttributeIds.FighterMissilesEm,
                    DogmaAttributeIds.FighterMissilesThermal, DogmaAttributeIds.FighterMissilesKinetic,
                    DogmaAttributeIds.FighterMissilesExplosive, DogmaAttributeIds.FighterMissilesDuration);
            total += perFighter * fighter.Quantity;
        }
        return total;
    }

    // One fighter ability's per-second damage: (Σ damage components) * multiplier / (duration_ms / 1000). Gated on the
    // type carrying the ability's damage multiplier (a support fighter has none) and a positive multiplier + duration, so
    // a non-damage ability resolves to 0. Mirrors _DamageOverCycle over the fighter ability attribute set.
    private double _FighterAbilityDps(DogmaItem fighter, int multiplierAttr, int emAttr, int thermalAttr,
        int kineticAttr, int explosiveAttr, int durationAttr)
    {
        if (!data.GetBaseAttributes(fighter.TypeId).Any(attribute => attribute.AttributeId == multiplierAttr))
            return 0;
        var multiplier = evaluator.Resolve(fighter, multiplierAttr);
        var duration = evaluator.Resolve(fighter, durationAttr);
        if (multiplier <= 0 || duration <= 0)
            return 0;
        var damage = evaluator.Resolve(fighter, emAttr) + evaluator.Resolve(fighter, thermalAttr)
                   + evaluator.Resolve(fighter, kineticAttr) + evaluator.Resolve(fighter, explosiveAttr);
        return damage * multiplier / (duration / 1000.0);
    }

    /// <summary>One <see cref="FighterContribution"/> per distinct launched fighter type: the per-fighter burst
    /// DPS and the engagement envelope for the per-squadron tooltip. Same resolve paths as the aggregate FighterDps.</summary>
    public IReadOnlyList<FighterContribution> CalculateFighterContributions(DogmaFit fit)
    {
        var contributions = new List<FighterContribution>();
        var seen = new HashSet<int>();
        foreach (var fighter in fit.Fighters)
        {
            if (!seen.Add(fighter.TypeId))
                continue;
            var attack = _FighterAbilityDps(fighter, DogmaAttributeIds.FighterAttackDamageMultiplier, DogmaAttributeIds.FighterAttackEm,
                DogmaAttributeIds.FighterAttackThermal, DogmaAttributeIds.FighterAttackKinetic,
                DogmaAttributeIds.FighterAttackExplosive, DogmaAttributeIds.FighterAttackDuration);
            var salvo = _FighterAbilityDps(fighter, DogmaAttributeIds.FighterMissilesDamageMultiplier, DogmaAttributeIds.FighterMissilesEm,
                DogmaAttributeIds.FighterMissilesThermal, DogmaAttributeIds.FighterMissilesKinetic,
                DogmaAttributeIds.FighterMissilesExplosive, DogmaAttributeIds.FighterMissilesDuration);
            contributions.Add(new FighterContribution(fighter.TypeId, attack + salvo,
                evaluator.Resolve(fighter, DogmaAttributeIds.FighterAttackOptimalRange),
                evaluator.Resolve(fighter, DogmaAttributeIds.FighterAttackFalloffRange),
                salvo > 0 ? evaluator.Resolve(fighter, DogmaAttributeIds.FighterMissilesRange) : 0,
                _FighterEwar(fighter)));
        }
        return contributions;
    }

    // A support fighter's EWAR readout (informational): each support type carries exactly one ability set, identified by
    // the presence of its strength attribute. Damage fighters carry none → null.
    private FighterEwar? _FighterEwar(DogmaItem fighter)
    {
        bool Has(int attributeId) => data.GetBaseAttributes(fighter.TypeId).Any(attribute => attribute.AttributeId == attributeId);
        double R(int attributeId) => evaluator.Resolve(fighter, attributeId);

        if (Has(DogmaAttributeIds.FighterNeutAmount))
            return new FighterEwar(FighterEwarKind.EnergyNeutralizer, R(DogmaAttributeIds.FighterNeutAmount),
                R(DogmaAttributeIds.FighterNeutOptimal), R(DogmaAttributeIds.FighterNeutFalloff));
        if (Has(DogmaAttributeIds.FighterEcmStrength))
            return new FighterEwar(FighterEwarKind.Ecm, R(DogmaAttributeIds.FighterEcmStrength),
                R(DogmaAttributeIds.FighterEcmOptimal), R(DogmaAttributeIds.FighterEcmFalloff));
        if (Has(DogmaAttributeIds.FighterPointStrength))
            return new FighterEwar(FighterEwarKind.WarpDisruption, R(DogmaAttributeIds.FighterPointStrength),
                R(DogmaAttributeIds.FighterPointRange), 0);
        if (Has(DogmaAttributeIds.FighterWebSpeedPenalty))
            return new FighterEwar(FighterEwarKind.StasisWeb, Math.Abs(R(DogmaAttributeIds.FighterWebSpeedPenalty)),
                R(DogmaAttributeIds.FighterWebOptimal), R(DogmaAttributeIds.FighterWebFalloff));
        return null;
    }

    // Fighter reload mapping, keyed on the squadron role (2270): how many shots a damaging ability
    // fires before rearming, and the per-shot rearm time (ms). The reload gap is the fixed refuel (2426) + rearm × numShots.
    private static readonly Dictionary<int, int> FighterNumShots = new() { [1] = 0, [2] = 12, [4] = 6, [5] = 3 };
    private static readonly Dictionary<int, int> FighterRearmMs = new() { [1] = 0, [2] = 4000, [4] = 6000, [5] = 20000 };

    // Reload-adjusted (sustained) fighter DPS — the combined cycle sequence. Verified against the reference: only the secondary
    // missile salvo (fighterAbilityMissiles, 2130-2182) carries charges and rearms; the primary attack ability
    // (fighterAbilityAttackMissile, 2226-2233) has no charges and would fire indefinitely. But the rearm halts the WHOLE
    // squadron, so both abilities idle during the shared refuel: over one sequence the salvo fires numShots over its
    // firing window, the attack fires ceil(window / attackCycle) shots, then the squadron refuels — each ability's DPS is
    // its damage-per-sequence over the sequence time. Without a reloading salvo, both abilities sustain their burst.
    private double FighterDpsSustained(DogmaFit fit)
    {
        double total = 0;
        foreach (var fighter in fit.Fighters)
            total += _SquadronSustainedDps(fighter) * fighter.Quantity;
        return total;
    }

    private double _SquadronSustainedDps(DogmaItem fighter)
    {
        var attackBurst = _FighterAbilityDps(fighter, DogmaAttributeIds.FighterAttackDamageMultiplier, DogmaAttributeIds.FighterAttackEm,
            DogmaAttributeIds.FighterAttackThermal, DogmaAttributeIds.FighterAttackKinetic,
            DogmaAttributeIds.FighterAttackExplosive, DogmaAttributeIds.FighterAttackDuration);
        var salvoBurst = _FighterAbilityDps(fighter, DogmaAttributeIds.FighterMissilesDamageMultiplier, DogmaAttributeIds.FighterMissilesEm,
            DogmaAttributeIds.FighterMissilesThermal, DogmaAttributeIds.FighterMissilesKinetic,
            DogmaAttributeIds.FighterMissilesExplosive, DogmaAttributeIds.FighterMissilesDuration);

        var numShots = FighterNumShots.GetValueOrDefault((int)evaluator.Resolve(fighter, DogmaAttributeIds.FighterSquadronRole));
        var salvoCycle = evaluator.Resolve(fighter, DogmaAttributeIds.FighterMissilesDuration);
        // No reloading salvo → nothing halts the squadron, both abilities sustain their burst.
        if (salvoBurst <= 0 || numShots <= 0 || salvoCycle <= 0)
            return attackBurst + salvoBurst;

        var role = (int)evaluator.Resolve(fighter, DogmaAttributeIds.FighterSquadronRole);
        var firingWindow = numShots * salvoCycle;                                   // the salvo fires this long...
        var refuelTime = evaluator.Resolve(fighter, DogmaAttributeIds.FighterRefuelTime)
                         + (double)FighterRearmMs.GetValueOrDefault(role) * numShots;   // ...then the squadron rearms
        var sequenceTime = firingWindow + refuelTime;

        var salvoSustained = salvoBurst * firingWindow / sequenceTime;
        var attackSustained = attackBurst;
        var attackCycle = evaluator.Resolve(fighter, DogmaAttributeIds.FighterAttackDuration);
        if (attackBurst > 0 && attackCycle > 0)
        {
            var attackShots = Math.Ceiling(firingWindow / attackCycle);             // shots fired before the shared rearm
            attackSustained = attackBurst * (attackShots * attackCycle) / sequenceTime;
        }
        return attackSustained + salvoSustained;
    }

    private const int MaxActiveDrones = 5;

    // Drones in space: only the drones that are actually deployed contribute DPS — at most 5 (the universal limit) and
    // only as many as fit the ship's drone bandwidth; bay drones beyond that just sit in the bay. We deploy in bay order,
    // filling the bandwidth, and report the active DPS, the bandwidth actually used and the active count so the panel
    // never shows more bandwidth used than the ship has.
    //   perDroneDps = sum(em/explosive/kinetic/thermal) * damageMultiplier / (cycle_ms / 1000)
    private (double Dps, double BandwidthUsed, int ActiveCount) Drones(DogmaFit fit)
    {
        var bandwidthBudget = evaluator.Resolve(fit.Ship, DogmaAttributeIds.DroneBandwidth);

        // Expand the bay to individual drone units (bandwidth + dps each).
        var units = new List<(double Bandwidth, double Dps)>();
        foreach (var drone in fit.Drones)
        {
            if (drone.CategoryId != DroneCategoryId)
                continue;
            var bandwidth = evaluator.Resolve(drone, DogmaAttributeIds.DroneBandwidthUse);
            var perDroneDps = _DamageOverCycle(drone, evaluator.Resolve(drone, DogmaAttributeIds.DamageMultiplier),
                evaluator.Resolve(drone, DogmaAttributeIds.CycleTime)).Dps;
            for (var i = 0; i < drone.Quantity; i++)
                units.Add((bandwidth, perDroneDps));
        }

        // Deploy the strongest drones that fit: at most 5, within the ship bandwidth (a fit puts its best drones in space).
        double remaining = bandwidthBudget, dps = 0, bandwidthUsed = 0;
        var active = 0;
        foreach (var unit in units.OrderByDescending(unit => unit.Dps))
        {
            if (active >= MaxActiveDrones)
                break;
            if (unit.Bandwidth > 0 && remaining < unit.Bandwidth)
                continue;   // does not fit; a smaller drone might still
            active++;
            remaining -= unit.Bandwidth;
            bandwidthUsed += unit.Bandwidth;
            dps += unit.Dps;
        }

        return (dps, bandwidthUsed, active);
    }

    // Mining yield (m³/s): every mining module plus the deployed mining drones. A mining item carries a positive
    // miningAmount harvested each duration cycle — the same attributes for mining lasers, strip miners, ice and gas
    // harvesters and mining drones, all resolved through the pipeline so ship/skill/crystal bonuses fold in. Only active
    // mining modules count toward the total (an offlined miner mines nothing), the same state-gate the weapon DPS uses.
    // Mining drones deploy strongest-first up to the 5-drone limit within the ship bandwidth, independently of combat drones — a fit mines
    // or fights, so the yield readout intentionally ignores the rare mixed-drone case.
    //   yield/s = miningAmount / (duration_ms / 1000), summed over mining modules + deployed mining drones
    private double MiningYield(DogmaFit fit)
    {
        // Only a running (active) mining module produces yield — an offlined or merely-onlined miner mines nothing, the
        // same gate the per-module mining/propulsion contributions use. Without it, offlining a miner left the total unchanged.
        var total = fit.Modules.Where(module => module.State >= ModuleState.Active).Sum(UnitMiningYield);

        var units = new List<(double Bandwidth, double Yield)>();
        foreach (var drone in fit.Drones)
        {
            if (drone.CategoryId != DroneCategoryId)
                continue;
            var yield = UnitMiningYield(drone);
            if (yield <= 0)
                continue;
            var bandwidth = evaluator.Resolve(drone, DogmaAttributeIds.DroneBandwidthUse);
            for (var i = 0; i < drone.Quantity; i++)
                units.Add((bandwidth, yield));
        }

        var remaining = evaluator.Resolve(fit.Ship, DogmaAttributeIds.DroneBandwidth);
        var active = 0;
        foreach (var unit in units.OrderByDescending(unit => unit.Yield))
        {
            if (active >= MaxActiveDrones)
                break;
            if (unit.Bandwidth > 0 && remaining < unit.Bandwidth)
                continue;   // does not fit; a smaller mining drone might still
            active++;
            remaining -= unit.Bandwidth;
            total += unit.Yield;
        }
        return total;
    }

    // One item's mining yield (m³/s): its resolved miningAmount over its duration cycle; 0 for a non-mining item.
    private double UnitMiningYield(DogmaItem item)
    {
        var amount = evaluator.Resolve(item, DogmaAttributeIds.MiningAmount);
        if (amount <= 0)
            return 0;
        var duration = evaluator.Resolve(item, DogmaAttributeIds.Duration);
        return duration > 0 ? amount / (duration / 1000.0) : 0;
    }

    // Loaded-turret DPS: each turret (a module whose type carries a damage multiplier) firing its loaded charge.
    //   perShot = sum(charge em/exp/kin/th) * weapon damageMultiplier;  dps = perShot / (cycleTime_ms / 1000)
    // Missiles (missileDamageMultiplier) and drones are a later phase.
    private double TurretDps(DogmaFit fit)
    {
        double total = 0;
        foreach (var module in fit.Modules)
        {
            if (module.State < ModuleState.Active || module.Charge is not { } charge || !IsTurret(module))
                continue;
            total += _DamageOverCycle(charge, evaluator.Resolve(module, DogmaAttributeIds.DamageMultiplier),
                evaluator.Resolve(module, DogmaAttributeIds.CycleTime)).Dps;
        }
        return total;
    }

    // Fully spooled turret DPS: an entropic disintegrator ramps its damage multiplier up to attr 2734
    // (damageMultiplierBonusMax) over its spool cycles. Every other turret lacks the attr (resolves to 0), so its max
    // equals its base DPS — TurretDpsMax therefore equals TurretDps unless a ramp weapon is fitted.
    private double TurretDpsMax(DogmaFit fit)
    {
        double total = 0;
        foreach (var module in fit.Modules)
        {
            if (module.State < ModuleState.Active || module.Charge is not { } charge || !IsTurret(module))
                continue;
            var dps = _DamageOverCycle(charge, evaluator.Resolve(module, DogmaAttributeIds.DamageMultiplier),
                evaluator.Resolve(module, DogmaAttributeIds.CycleTime)).Dps;
            total += dps * (1 + RampBonus(module));
        }
        return total;
    }

    // The entropic-disintegrator spool bonus (attr 2734), or 0 when the weapon does not carry it. Gated on the attribute
    // being present on the type: a plain Resolve returns the attribute's 0.5 default and would spuriously ramp every
    // weapon by 1.5x. Only a disintegrator actually carries 2734.
    private double RampBonus(DogmaItem module) =>
        data.GetBaseAttributes(module.TypeId).Any(attribute => attribute.AttributeId == DogmaAttributeIds.DamageMultiplierBonusMax)
            ? evaluator.Resolve(module, DogmaAttributeIds.DamageMultiplierBonusMax)
            : 0;

    // A turret is a module whose type intrinsically carries a damage multiplier (excludes non-weapon modules that
    // happen to hold a charge, e.g. scripted modules).
    private bool IsTurret(DogmaItem module) =>
        data.GetBaseAttributes(module.TypeId).Any(attribute => attribute.AttributeId == DogmaAttributeIds.DamageMultiplier);

    // A module is an energy nosferatu (gives us capacitor) when it carries an energyNosferatu effect — this excludes
    // energy neutralisers (energyNeutralizer, drain only, no self income) and remote capacitor transmitters.
    private bool IsNosferatu(DogmaItem module) =>
        data.GetTypeEffects(module.TypeId)
            .Any(typeEffect => data.GetEffect(typeEffect.EffectId)?.Name
                .StartsWith("energyNosferatu", StringComparison.OrdinalIgnoreCase) == true);

    // Remote assistance aggregate: sums rep/s per layer and cap/s over all active remote modules;
    // range is the maximum projection range among modules of each type (representative best-case).
    private (double ArmorRepPerSec, double ShieldRepPerSec, double HullRepPerSec, double CapPerSec,
             double ArmorRangeMeters, double ShieldRangeMeters, double HullRangeMeters, double CapRangeMeters)
        RemoteAssistance(DogmaFit fit)
    {
        double armorRep = 0, shieldRep = 0, hullRep = 0, capTransfer = 0;
        double armorRange = 0, shieldRange = 0, hullRange = 0, capRange = 0;

        // Accumulate one item's remote contribution. Shared by modules and logistic drones. Multiplied by
        // Quantity so a stack of N logistic drones counts N times (modules are Quantity 1). NB v1 does not yet model
        // the drone bandwidth / max-5-active deployment cap for logi drones — a bay with >5 drones may overstate.
        void Accumulate(DogmaItem item)
        {
            var qty = item.Quantity;
            if (_RemoteRepair(item) is { } rep)
            {
                switch (rep.RepairLayer)
                {
                    case RepairLayer.Armor:
                        armorRep += rep.RepPerSec * qty;
                        if (rep.RemoteRangeMeters > armorRange) armorRange = rep.RemoteRangeMeters;
                        break;
                    case RepairLayer.Shield:
                        shieldRep += rep.RepPerSec * qty;
                        if (rep.RemoteRangeMeters > shieldRange) shieldRange = rep.RemoteRangeMeters;
                        break;
                    case RepairLayer.Hull:
                        hullRep += rep.RepPerSec * qty;
                        if (rep.RemoteRangeMeters > hullRange) hullRange = rep.RemoteRangeMeters;
                        break;
                }
                return;
            }

            if (_RemoteCapTransfer(item) is { } capXfer)
            {
                capTransfer += capXfer.CapPerSec * qty;
                if (capXfer.RemoteRangeMeters > capRange) capRange = capXfer.RemoteRangeMeters;
            }
        }

        foreach (var module in fit.Modules)
            if (module.State >= ModuleState.Active)
                Accumulate(module);

        // Logistic drones (Logistic Drone group, category 18) project remote reps when active.
        foreach (var drone in fit.Drones)
            if (drone.CategoryId == DroneCategoryId && drone.State >= ModuleState.Active)
                Accumulate(drone);

        return (armorRep, shieldRep, hullRep, capTransfer, armorRange, shieldRange, hullRange, capRange);
    }

    // EHP for one layer under a damage profile: HP / Σ(wᵢ · resonanceᵢ).
    // resonanceAttributes is ordered [EM, Th, Kin, Exp] — matches DogmaAttributeIds.*Resonance arrays.
    // With DamageProfile.Uniform (0.25 each) this is mathematically identical to the old mean-resonance formula.
    // An all-zero profile (0/0/0/0) is the "Raw HP" mode: resists are ignored so the layer reports its raw buffer HP,
    // an NPC-independent baseline.
    private double LayerEhp(DogmaItem ship, int hitPointsAttribute, int[] resonanceAttributes, DamageProfile profile)
    {
        var hitPoints = evaluator.Resolve(ship, hitPointsAttribute);
        if (hitPoints <= 0)
            return 0;
        if (profile.Em + profile.Th + profile.Kin + profile.Exp <= 0)
            return hitPoints;
        var weightedResonance =
            profile.Em  * evaluator.Resolve(ship, resonanceAttributes[0]) +
            profile.Th  * evaluator.Resolve(ship, resonanceAttributes[1]) +
            profile.Kin * evaluator.Resolve(ship, resonanceAttributes[2]) +
            profile.Exp * evaluator.Resolve(ship, resonanceAttributes[3]);
        return weightedResonance > 0 ? hitPoints / weightedResonance : 0;
    }

    private static double Round2(double value) => Math.Round(value, 2);
}
