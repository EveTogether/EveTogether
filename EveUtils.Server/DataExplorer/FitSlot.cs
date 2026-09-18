namespace EveUtils.Server.DataExplorer;

/// <summary>Where an item of a fit sits, read from its ESI <c>flag</c>. In the order a fitting window lists them.</summary>
public enum FitSlot
{
    High = 0,
    Mid = 1,
    Low = 2,
    Rig = 3,
    Subsystem = 4,
    Drones = 5,
    Fighters = 6,
    Cargo = 7,
    Other = 8,
}
