namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// One fitted module's or drone's own resolved contribution to the fit: the per-item readout behind the
/// per-module tooltip. Computed on the exact same resolve paths as the aggregate <see cref="DerivedStats"/> (no second
/// DPS/yield formula). Only the fields relevant to <see cref="Kind"/> are populated; the rest stay zero.
/// </summary>
public sealed record ModuleContribution(
    int TypeId,
    ModuleContributionKind Kind,
    ModuleState State,
    bool IsDrone = false,
    int? ChargeTypeId = null,
    double Dps = 0,
    double DamageEm = 0,
    double DamageThermal = 0,
    double DamageKinetic = 0,
    double DamageExplosive = 0,
    double MiningYieldPerSec = 0,
    double M3PerCycle = 0,
    double SpeedBoostPercent = 0,
    // Tooltip pass: the in-game engagement/utility readout. Turret + drone carry optimal/falloff/tracking; local
    // reps carry rep/s + the layer they restore; cap modules (nos/transfer/booster) carry cap/s into our own capacitor.
    // Remote modules carry rep/s or cap/s projected onto a target, with the projection range in metres.
    double OptimalRange = 0,
    double FalloffRange = 0,
    double TrackingSpeed = 0,
    double RepPerSec = 0,
    RepairLayer RepairLayer = RepairLayer.None,
    double CapPerSec = 0,
    double RemoteRangeMeters = 0,
    // Fully spooled DPS for an entropic disintegrator (damage ramped to its max, attr 2734); equals Dps for non-ramp weapons.
    double DpsMax = 0);
