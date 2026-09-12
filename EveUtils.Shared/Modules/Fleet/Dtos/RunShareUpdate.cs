namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// What one pilot shares of their own run with the fleet, as it stands right now (ET-242, mining added by ET-234):
/// whether their loot, bounty and mining go to the fleet at all, and — when loot or mining do — the figures that
/// count on their run. The wire payload of <see cref="Events.FleetRunShareEvent"/>; the pilot is the envelope's own
/// character, never a field of this.
///
/// The whole state every time rather than a change: a burst of captures goes out as one message, a member whose window
/// opened late has everything with the next one, and no order two messages can arrive in leaves a wrong list standing.
/// <see cref="UnixMs"/> is what lets a receiver keep the newest.
///
/// No ISK in it: the loot figure already travels as <c>MetricKind.Loot</c>, and a receiver values these items by type
/// id from its own price cache, the way every loot line in this app is valued. <see cref="SharesBounty"/> carries no
/// figure either — only that the bounty the metric stream carried is no longer offered, so it can be taken down.
///
/// <see cref="MinedUnits"/>/<see cref="ResidueUnits"/> are this run's own totals, summed across every ore — the
/// residue split <c>MetricKind.MiningYield</c>'s single scalar cannot carry, and the one MINING's "fleet mined" and
/// "remaining" lines need. Zero while <see cref="SharesMining"/> is false, the same as an empty <see cref="Loot"/>.
/// </summary>
/// <param name="CaptureCount">How many of the pilot's captures the list was counted from.</param>
/// <param name="Loot">The items that count, one line per kind — empty while <see cref="SharesLoot"/> is false.</param>
/// <param name="MinedUnits">This run's total mined units (crit included) — 0 while <see cref="SharesMining"/> is
/// false.</param>
/// <param name="ResidueUnits">This run's total residue units — 0 while <see cref="SharesMining"/> is false.</param>
public sealed record RunShareUpdate(
    long FleetId,
    string GroupCode,
    long UnixMs,
    bool SharesLoot,
    bool SharesBounty,
    int CaptureCount,
    IReadOnlyList<RunShareLootLine> Loot,
    bool SharesMining = false,
    int MinedUnits = 0,
    int ResidueUnits = 0);
