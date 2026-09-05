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
    /// <summary>The destination system as the pilot typed it — free text. Resolved further by
    /// <see cref="EscalationSolarSystemId"/> when the SDE recognises the name (ET-127); still stored plainly when
    /// it does not, since a destination the catalogue has never seen is not an error (AC-2).</summary>
    EscalationSystem,
    /// <summary>The destination's own <c>solarSystemId</c>, resolved locally off the SDE's <c>SolarSystem</c> table
    /// — never re-derived from <see cref="EscalationSystem"/>'s name again later, the same reasoning as
    /// <see cref="EscalationDungeonId"/> (ET-127). Feeds the jump count computed at display time; the security
    /// status is not stored, since it is resolved from the same name for free whenever it is shown.</summary>
    EscalationSolarSystemId,
    /// <summary>The computed UTC deadline, never a default duration (ET-125 AC-3): the pilot carries over whatever
    /// remaining time the Agency showed, and this is that reading turned into a moment.</summary>
    EscalationExpiresAtUtc
}
