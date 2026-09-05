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
    Escalation,
    /// <summary>The escalation's own <c>dungeonId</c>, alongside <see cref="Escalation"/>'s name — never derived
    /// from the name again later, since two catalogue sites can share a name across archetypes (ET-125 AC-2).</summary>
    EscalationDungeonId,
    /// <summary>The destination system as the pilot typed it — free text, resolved no further than that (ET-127).</summary>
    EscalationSystem,
    /// <summary>The computed UTC deadline, never a default duration (ET-125 AC-3): the pilot carries over whatever
    /// remaining time the Agency showed, and this is that reading turned into a moment.</summary>
    EscalationExpiresAtUtc
}
