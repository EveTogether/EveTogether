namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// The derived fit stats. CPU/power are rounded to two decimals (P-1, so fitting checks line up with the reference). EHP is
/// computed per layer under a uniform 25/25/25/25 damage profile (P-2). <see cref="TurretDps"/> / <see cref="MissileDps"/>
/// / <see cref="DroneDps"/> cover loaded turrets, missile launchers and drones in space. Capacitor stats come from the
/// discrete-event <see cref="CapacitorSimulator"/>: when <see cref="CapacitorStable"/> the cap settles at
/// <see cref="CapacitorStablePercent"/>, otherwise it runs dry after <see cref="CapacitorDepletesInSeconds"/>.
/// </summary>
public sealed record DerivedStats(
    double CpuUsed,
    double CpuOutput,
    double PowerUsed,
    double PowerOutput,
    double MaxVelocity,
    double SignatureRadius,
    double AlignTime,
    double ShieldEhp,
    double ArmorEhp,
    double StructureEhp,
    double Ehp,
    double TurretDps,
    double DroneDps,
    double MissileDps,
    double CapacitorCapacity,
    double CapacitorRecharge,
    double CapacitorUsed,
    bool CapacitorStable,
    double CapacitorStablePercent,
    double CapacitorDepletesInSeconds,
    double DroneBandwidthUsed,
    int DroneActiveCount,
    double MiningYield,
    // Remote assistance: HP/s and GJ/s projected onto allies, plus the representative (max) range per type.
    double RemoteArmorRepPerSec = 0,
    double RemoteShieldRepPerSec = 0,
    double RemoteHullRepPerSec = 0,
    double RemoteCapPerSec = 0,
    double RemoteArmorRangeMeters = 0,
    double RemoteShieldRangeMeters = 0,
    double RemoteHullRangeMeters = 0,
    double RemoteCapRangeMeters = 0,
    // Fully spooled turret DPS (entropic disintegrators at max ramp); equals TurretDps when no ramp weapon is fitted.
    double TurretDpsMax = 0,
    // Reload-adjusted sustained turret/missile DPS; equals the burst value for weapons without a clip/reload (lasers, drones).
    double TurretDpsSustained = 0,
    double MissileDpsSustained = 0,
    // Launched fighter squadron DPS (carriers/supercarriers/structures); 0 when no fighters are launched.
    double FighterDps = 0,
    // Reload-adjusted (sustained) fighter DPS — lower than the burst when an ability rearms between clips.
    double FighterDpsSustained = 0)
{
    /// <summary>Turret + drone + missile + fighter DPS.</summary>
    public double TotalDps => TurretDps + DroneDps + MissileDps + FighterDps;

    /// <summary>Total DPS with turrets fully spooled (entropic disintegrators at max ramp). Equals <see cref="TotalDps"/>
    /// when no ramp weapon is fitted.</summary>
    public double TotalDpsMax => TurretDpsMax + DroneDps + MissileDps + FighterDps;

    /// <summary>Total DPS adjusted for weapon reloads (drones sustain their burst). Equals <see cref="TotalDps"/> when no
    /// weapon has a reload gap (lasers, drone-only fits). Fighters sustain their burst here.</summary>
    public double TotalDpsSustained => TurretDpsSustained + DroneDps + MissileDpsSustained + FighterDpsSustained;

    /// <summary>Peak recharge minus load (GJ/s) — positive means the capacitor gains on its drain at the peak.</summary>
    public double CapacitorDelta => CapacitorRecharge - CapacitorUsed;

    /// <summary>True when the fit carries at least one active remote-repair or remote cap-transfer module.</summary>
    public bool HasRemoteAssistance =>
        RemoteArmorRepPerSec > 0 || RemoteShieldRepPerSec > 0 || RemoteHullRepPerSec > 0 || RemoteCapPerSec > 0;
}
