namespace EveUtils.Shared.Modules.Gamelog.Models;

public sealed record MiningEvent(
    DateTime Timestamp,
    int Units,
    string OreType,
    bool IsCritical,
    int LostResidue) : GameLogEvent(Timestamp);
