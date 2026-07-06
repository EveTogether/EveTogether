namespace EveUtils.Shared.Modules.Gamelog.Models;

public sealed record LocationEvent(
    DateTime Timestamp,
    string System) : GameLogEvent(Timestamp);
