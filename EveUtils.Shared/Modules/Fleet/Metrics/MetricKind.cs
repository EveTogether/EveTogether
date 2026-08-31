namespace EveUtils.Shared.Modules.Fleet.Metrics;

/// <summary>
/// What a fleet activity sample measures. Deliberately broad and open-for-extension: new kinds
/// (salvage, reps, …) can be added without a protocol change. v1 actively produces <see cref="Dps"/> and
/// <see cref="DpsIn"/>; the rest are reserved examples whose semantics are already declared in
/// <see cref="FleetMetricCatalog"/>.
/// </summary>
public enum MetricKind
{
    /// <summary>Damage dealt per second (the outgoing DPS series of a member's live graph).</summary>
    Dps = 0,
    MiningYield = 1,
    Bounty = 2,
    Location = 3,

    /// <summary>Damage received per second (the incoming DPS series). The paired counterpart of <see cref="Dps"/>,
    /// carried as its own kind so the single-scalar sample envelope stays generic.</summary>
    DpsIn = 4,

    /// <summary>Energy neutralized per second (cap-warfare activity, both applied and received combined). A Rate,
    /// drawn as its own line on a member's live combat graph alongside <see cref="Dps"/>/<see cref="DpsIn"/>.</summary>
    Neut = 5,

    /// <summary>Running total of ore mined this session, sourced from the ESI mining-ledger endpoint. A
    /// Cumulative rollup into the fleet's total haul. Reserved: the descriptor travels the bus now; the ESI
    /// integration that feeds it is a later seam.</summary>
    MiningLedger = 6,

    /// <summary>Remote capacitor transmitted per second (cap support activity, given and received combined). A Rate,
    /// drawn as its own line on a member's live combat graph.</summary>
    Cap = 7,

    /// <summary>
    /// Energy neutralized <b>on</b> this member per second — the receiving half of <see cref="Neut"/> on its own.
    /// <see cref="Neut"/> deliberately combines both directions, which makes it the right line for "is there cap
    /// warfare on this member's graph" and the wrong figure for "who is being neuted": the pilot with the highest
    /// combined rate may well be the one doing the neuting. A window that names a pilot to act on needs the
    /// direction, so it travels as its own kind rather than as a second meaning of an existing one (ET-72).
    ///
    /// Not drawn as a graph line: the combined <see cref="Neut"/> line already occupies that slot on every existing
    /// screen, and doubling it would say the same thing twice.
    /// </summary>
    NeutIn = 8,

    /// <summary>
    /// Whether the publishing character is in game, as their own client sees it — a <see cref="PresenceState"/> in
    /// the sample's value. A State kind: it is a label about the pilot, never a figure to roll up.
    ///
    /// It rides the existing 1 Hz stream rather than a channel of its own precisely because the stream's arrival is
    /// half the answer. A sample says "my EVE Together is running" by existing at all, and says whether EVE itself is
    /// running by its value; a client that has been closed sends neither, which is the case no message can ever
    /// report and <see cref="FleetMemberPresence.SilentAfter"/> reads instead (ET-70).
    /// </summary>
    Presence = 9,
}
