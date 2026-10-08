namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>
/// One of the eight per-second series a run keeps (ET-467): the axes of the live meter, each way round on its own.
/// Stored by value, so members are only ever appended.
/// </summary>
public enum CombatSeriesKind
{
    DmgOut,
    DmgIn,
    RepOut,
    RepIn,
    NeutOut,
    NeutIn,
    CapOut,
    CapIn
}
