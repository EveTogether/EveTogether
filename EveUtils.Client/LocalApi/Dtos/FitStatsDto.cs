using EveUtils.Client.ViewModels.FitBrowser;

namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// A compact, stable subset of the Dogma-computed fit stats for the API (the full <see cref="FitStats"/> record has
/// dozens of internal fields). Computed at all-level-5 skills, like the fit-detail window.
/// </summary>
public sealed record FitStatsDto(
    double TotalDps,
    double WeaponDps,
    double DroneDps,
    double Ehp,
    double ShieldEhp,
    double ArmorEhp,
    double StructureEhp,
    bool CapacitorStable,
    double CapacitorStablePercent,
    double CapacitorCapacity,
    double TargetingRange,
    double ScanResolution,
    double MaxVelocity,
    double AlignTime,
    double SignatureRadius,
    int ActiveDroneCount)
{
    public static FitStatsDto FromStats(FitStats s) => new(
        s.TotalDps, s.WeaponDps, s.DroneDps,
        s.Ehp, s.ShieldEhp, s.ArmorEhp, s.StructureEhp,
        s.CapacitorStable, s.CapacitorStablePercent, s.CapacitorCapacity,
        s.TargetingRange, s.ScanResolution,
        s.MaxVelocity, s.AlignTime, s.SignatureRadius,
        s.ActiveDroneCount);
}
