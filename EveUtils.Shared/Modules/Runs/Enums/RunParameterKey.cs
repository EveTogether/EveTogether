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
    EscalationExpiresAtUtc,
    /// <summary>A clipboard line whose reward form is not yet classified. Its unmodified text stays in
    /// <see cref="Entities.RunParameter.TypedValue"/> so it can be classified later.</summary>
    Unknown,

    /// <summary>The abyssal pocket's own tier and weather (ET-241), chosen in the run window and otherwise lost at
    /// save — never a reward, so <c>GetActivityOverviewQueryHandler</c> keeps it out of the reward-chip list on
    /// purpose. <see cref="Entities.RunParameter.TypedValue"/> holds <c>"{tier index}|{weather name}"</c>; the tier
    /// word itself (e.g. "Agitated") is looked up from the index at read time, never stored as text, so renaming a
    /// tier only ever means changing one list.</summary>
    AbyssalFilament,

    /// <summary>The clipboard capture opened with EVE's own warning sentence for an important (storyline) mission
    /// (ET-251) — never a reward, so MISSION shows it as its own fact rather than folding it into
    /// <see cref="Entities.RunParameter.TypedValue"/>'s reward rows.</summary>
    ImportantMission,

    /// <summary>The mission's own destination system, from the Objectives block's plain "Location" line (e.g.
    /// "0.6 Aphend" parses down to "Aphend") — never a reward. This is the mission's own target, not the pilot's
    /// own system at run start (<c>Run.SolarSystemId</c>); shown only where the SDE recognises the name exactly
    /// (ET-253).</summary>
    MissionLocation,

    /// <summary>The abyssal filament CONSUMABLES resolved to spend, as an SDE type id (ET-249) — resolved once at
    /// SAVE from the pocket's own stored tier and weather (<see cref="AbyssalFilament"/>) and never re-looked-up,
    /// the same "captured once" rule every other snapshot in this enum follows. Absent when the tier or weather was
    /// never set, or the SDE has no type by that exact name — never a guessed type id.</summary>
    AbyssalFilamentTypeId,

    /// <summary>How many filaments CONSUMABLES charges the run for (ET-249) — a proposal from the fit's hull class,
    /// left for the pilot to change, and never a reward. Absent when nothing was ever confirmed (no fit known, no
    /// hull-class rule for it), so a run with no known count is silent rather than costed at zero.</summary>
    AbyssalFilamentCount
}
