namespace EveUtils.Shared.Modules.Dogma;

/// <summary>Which layer a local-repair module restores, so the per-module tooltip can label its rep/s line
/// "shield boosted" / "armor repaired" / "hull repaired". <see cref="None"/> for non-repair contributions.</summary>
public enum RepairLayer
{
    None,
    Shield,
    Armor,
    Hull
}
