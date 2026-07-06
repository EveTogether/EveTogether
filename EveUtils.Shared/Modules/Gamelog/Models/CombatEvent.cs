namespace EveUtils.Shared.Modules.Gamelog.Models;

public sealed record CombatEvent(
    DateTime Timestamp,
    DamageDirection Direction,
    int Amount,
    string Target,
    string? Weapon,
    HitQuality Quality) : GameLogEvent(Timestamp);
