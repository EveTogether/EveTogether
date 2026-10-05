namespace EveUtils.Shared.Modules.Skills;

/// <summary>The stats <see cref="SkillImpactScanner"/> can price a skill against — the D3 table from ET-356's
/// grooming, fifteen values across five groups (Offense/Tank/Capacitor/Navigation/Targeting/Fitting).</summary>
public enum SkillImpactStat
{
    Dps,
    DroneDps,
    Optimal,
    Falloff,
    Tracking,
    Ehp,
    Capacitor,
    Speed,
    AlignTime,
    Signature,
    LockRange,
    ScanResolution,
    SensorStrength,
    FreeCpu,
    FreePg,
}
