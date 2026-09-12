namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// What one pilot shares of their own run with the fleet, as it stands right now (ET-242): whether their loot and their
/// bounty go to the fleet at all, and — when loot does — the items that count on their run. The wire payload of
/// <see cref="Events.FleetRunShareEvent"/>; the pilot is the envelope's own character, never a field of this.
///
/// The whole state every time rather than a change: a burst of captures goes out as one message, a member whose window
/// opened late has everything with the next one, and no order two messages can arrive in leaves a wrong list standing.
/// <see cref="UnixMs"/> is what lets a receiver keep the newest.
///
/// No ISK in it: the figure already travels as <c>MetricKind.Loot</c>, and a receiver values these items by type id
/// from its own price cache, the way every loot line in this app is valued. <see cref="SharesBounty"/> carries no figure
/// either — only that the bounty the metric stream carried is no longer offered, so it can be taken down.
/// </summary>
/// <param name="CaptureCount">How many of the pilot's captures the list was counted from.</param>
/// <param name="Loot">The items that count, one line per kind — empty while <see cref="SharesLoot"/> is false.</param>
public sealed record RunShareUpdate(
    long FleetId,
    string GroupCode,
    long UnixMs,
    bool SharesLoot,
    bool SharesBounty,
    int CaptureCount,
    IReadOnlyList<RunShareLootLine> Loot);
