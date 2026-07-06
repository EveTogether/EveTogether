using System.Collections.Generic;
using EveUtils.Shared.Modules.Dogma;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// The computed stats of a fit, produced by the Dogma engine at all-level-5. Grouped to match the
/// detail window's panels: firepower, resource usage, defense (EHP + per-layer resists), capacitor, targeting,
/// navigation and drones. Raw numbers; the view-model formats them for display.
/// </summary>
public sealed record FitStats(
    // Firepower
    double TotalDps,
    double WeaponDps,
    double DroneDps,
    // Resource usage (used / available)
    double CpuUsed,
    double CpuOutput,
    double PowerUsed,
    double PowerOutput,
    double DroneBayUsed,
    double DroneBayAvailable,
    double DroneBandwidthUsed,
    double DroneBandwidthAvailable,
    double CalibrationUsed,
    double CalibrationAvailable,
    // Defense
    double Ehp,
    double ShieldEhp,
    double ArmorEhp,
    double StructureEhp,
    ResistLayer ShieldResists,
    ResistLayer ArmorResists,
    ResistLayer StructureResists,
    // Capacitor
    bool CapacitorStable,
    double CapacitorStablePercent,
    double CapacitorDepletesInSeconds,
    double CapacitorCapacity,
    double CapacitorDelta,
    double CapacitorRecharge,
    // Targeting
    double TargetingRange,
    double ScanResolution,
    double MaxLockedTargets,
    double SensorStrength,
    // Navigation
    double MaxVelocity,
    double Mass,
    double Agility,
    double AlignTime,
    double WarpSpeed,
    double SignatureRadius,
    // Drones
    int ActiveDroneCount,
    // Mining (m³/s; 0 for a non-mining fit, which hides the panel)
    double MiningYield,
    // Per-module contributions: each fitted module's/drone's own resolved stats, for the per-module tooltip (later UI).
    IReadOnlyList<ModuleContribution> ModuleContributions,
    // Remote assistance: HP/s and GJ/s projected onto allies, with the max projection range per type.
    double RemoteArmorRepPerSec = 0,
    double RemoteShieldRepPerSec = 0,
    double RemoteHullRepPerSec = 0,
    double RemoteCapPerSec = 0,
    double RemoteArmorRangeMeters = 0,
    double RemoteShieldRangeMeters = 0,
    double RemoteHullRangeMeters = 0,
    double RemoteCapRangeMeters = 0,
    // Firepower breakdown + entropic disintegrator spool-up, for the OFFENSE panel range and its split tooltip.
    double TurretDps = 0,
    double MissileDps = 0,
    double TurretDpsMax = 0,
    double TotalDpsMax = 0,
    // Reload-adjusted sustained turret/missile DPS, shown as the "(reload …)" note in the OFFENSE breakdown.
    double TurretDpsSustained = 0,
    double MissileDpsSustained = 0,
    // Launched fighter squadron DPS (carriers/supercarriers/structures); 0 hides the Fighters line.
    double FighterDps = 0,
    // Reload-adjusted (sustained) fighter DPS, shown as the "(reload …)" note on the Fighters breakdown line.
    double FighterDpsSustained = 0,
    // Per launched fighter type: the per-fighter DPS + range for the per-squadron tooltip.
    IReadOnlyList<FighterContribution>? FighterContributions = null)
{
    /// <summary>True when the fit carries at least one active remote-repair or remote cap-transfer module.</summary>
    public bool HasRemoteAssistance =>
        RemoteArmorRepPerSec > 0 || RemoteShieldRepPerSec > 0 || RemoteHullRepPerSec > 0 || RemoteCapPerSec > 0;
}
