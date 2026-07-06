using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// <see cref="IFitStatsProvider"/> backed by the Dogma engine. Maps an <see cref="EsiFitting"/> to a
/// <see cref="FitInput"/> — pairing each module with the charge sitting in its slot, and the drone-bay items as drones
/// — runs the calculator at all-level-5 (the validation baseline), and reads the headline stats plus the per-layer
/// resists and ship-level targeting/navigation/drone attributes the panels show.
/// </summary>
public sealed class DogmaFitStatsProvider(IDogmaCalculator calculator, ISdeAccessor sde, IDogmaDataAccessor data)
    : IFitStatsProvider, ISingletonService
{
    private const int DroneCategoryId = 18;

    // Ship attributes the panels read directly (not in DerivedStats). CCP-stable ids.
    private const int MaxTargetRange = 76;
    private const int ScanResolution = 564;
    private const int MaxLockedTargets = 192;
    private static readonly int[] SensorStrengths = [208, 209, 210, 211]; // radar, ladar, magnetometric, gravimetric
    private const int Agility = 70;
    private const int WarpSpeedMultiplier = 600;
    private const int DroneCapacityAttr = 283;       // drone bay m3 (ship)
    private const int DroneBandwidthAttr = 1271;     // drone bandwidth (ship)

    public Task<FitStats?> ComputeAsync(EsiFitting fit, CancellationToken cancellationToken = default) =>
        ComputeAsync(fit, FitInputMapper.BuildModules(fit, sde, data), cancellationToken: cancellationToken);

    public async Task<FitStats?> ComputeAsync(EsiFitting fit, IReadOnlyList<ModuleInput> modules,
        int? tacticalModeTypeId = null, IReadOnlyList<DroneInput>? activeDrones = null,
        IReadOnlyList<ImplantInput>? boosters = null, SkillSource? skills = null,
        DamageProfile? profile = null, WeatherInput? weather = null,
        IReadOnlyList<FighterInput>? activeFighters = null, CancellationToken cancellationToken = default)
    {
        if (!sde.IsAvailable) return null;

        try
        {
            // Explicit user selection drives drone DPS; null = the engine auto-deploys the strongest the bandwidth fits.
            var drones = activeDrones ?? FitInputMapper.BuildDrones(fit);
            var droneItems = fit.Items.Where(i => i.Flag.StartsWith("DroneBay", StringComparison.OrdinalIgnoreCase)).ToList();

            var result = await calculator.CalculateAsync(
                new FitInput(fit.ShipTypeId, modules, skills ?? SkillSource.AllLevelFive, drones, boosters,
                    TacticalModeTypeId: tacticalModeTypeId, Profile: profile, Weather: weather, Fighters: activeFighters),
                cancellationToken);
            var d = result.Derived;

            var droneBayUsed = droneItems.Sum(i => (sde.GetType(i.TypeId)?.Volume ?? 0) * i.Quantity);
            var (calibrationUsed, calibrationTotal) = FitResourceMath.Calibration(fit, data);

            return new FitStats(
                TotalDps: d.TotalDps,
                WeaponDps: d.TurretDps + d.MissileDps,
                DroneDps: d.DroneDps,
                TurretDps: d.TurretDps,
                MissileDps: d.MissileDps,
                TurretDpsMax: d.TurretDpsMax,
                TotalDpsMax: d.TotalDpsMax,
                TurretDpsSustained: d.TurretDpsSustained,
                MissileDpsSustained: d.MissileDpsSustained,
                FighterDps: d.FighterDps,
                FighterDpsSustained: d.FighterDpsSustained,
                FighterContributions: result.FighterContributions,
                CpuUsed: d.CpuUsed, CpuOutput: d.CpuOutput,
                PowerUsed: d.PowerUsed, PowerOutput: d.PowerOutput,
                DroneBayUsed: droneBayUsed, DroneBayAvailable: result.ShipAttribute(DroneCapacityAttr),
                DroneBandwidthUsed: d.DroneBandwidthUsed, DroneBandwidthAvailable: result.ShipAttribute(DroneBandwidthAttr),
                CalibrationUsed: calibrationUsed, CalibrationAvailable: calibrationTotal,
                Ehp: d.Ehp, ShieldEhp: d.ShieldEhp, ArmorEhp: d.ArmorEhp, StructureEhp: d.StructureEhp,
                ShieldResists: Resists(result, DogmaAttributeIds.ShieldResonance),
                ArmorResists: Resists(result, DogmaAttributeIds.ArmorResonance),
                StructureResists: Resists(result, DogmaAttributeIds.StructureResonance),
                CapacitorStable: d.CapacitorStable,
                CapacitorStablePercent: d.CapacitorStablePercent,
                CapacitorDepletesInSeconds: d.CapacitorDepletesInSeconds,
                CapacitorCapacity: d.CapacitorCapacity,
                CapacitorDelta: d.CapacitorDelta,
                CapacitorRecharge: d.CapacitorRecharge,
                TargetingRange: result.ShipAttribute(MaxTargetRange),
                ScanResolution: result.ShipAttribute(ScanResolution),
                MaxLockedTargets: result.ShipAttribute(MaxLockedTargets),
                SensorStrength: SensorStrengths.Max(result.ShipAttribute),
                MaxVelocity: d.MaxVelocity,
                Mass: result.ShipAttribute(DogmaAttributeIds.Mass),
                Agility: result.ShipAttribute(Agility),
                AlignTime: d.AlignTime,
                WarpSpeed: result.ShipAttribute(WarpSpeedMultiplier),
                SignatureRadius: d.SignatureRadius,
                ActiveDroneCount: d.DroneActiveCount,
                MiningYield: d.MiningYield,
                ModuleContributions: result.Contributions,
                RemoteArmorRepPerSec: d.RemoteArmorRepPerSec,
                RemoteShieldRepPerSec: d.RemoteShieldRepPerSec,
                RemoteHullRepPerSec: d.RemoteHullRepPerSec,
                RemoteCapPerSec: d.RemoteCapPerSec,
                RemoteArmorRangeMeters: d.RemoteArmorRangeMeters,
                RemoteShieldRangeMeters: d.RemoteShieldRangeMeters,
                RemoteHullRangeMeters: d.RemoteHullRangeMeters,
                RemoteCapRangeMeters: d.RemoteCapRangeMeters);
        }
        catch
        {
            return null;
        }
    }

    private static ResistLayer Resists(FitResult result, int[] resonances) => new(
        Em: Percent(result.ShipAttribute(resonances[0])),
        Thermal: Percent(result.ShipAttribute(resonances[1])),
        Kinetic: Percent(result.ShipAttribute(resonances[2])),
        Explosive: Percent(result.ShipAttribute(resonances[3])));

    private static double Percent(double resonance) => (1.0 - resonance) * 100.0;
}
