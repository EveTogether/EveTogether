namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>What a <see cref="Entities.RunParameter"/> row observed. Stored by value, so members are only ever
/// appended — the two mission parameters below keep 0 and 1 forever. Everything from <see cref="Isk"/> onwards is a
/// reward form (ET-137).</summary>
public enum RunParameterKey
{
    Smugglers,
    Civilians,
    Isk,
    BonusIsk,
    Bounty,
    FixedPayout,
    Escrow,
    LoyaltyPoints,
    Evermarks,
    Item,
    Loot,
    Standings,
    Filament,
    Escalation
}
