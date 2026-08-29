namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>
/// A site (dungeon) from the SDE catalogue with its archetype and faction resolved (ET-36). Everything but the id
/// and the name is optional: the empty case is the normal one, so present an absent value as absent.
/// </summary>
/// <param name="ArchetypeId">CCP's own categorisation ("what kind of site is this"), the only reliable such axis.</param>
/// <param name="ArchetypeName">Null for archetype 43, which the SDE defines without a title.</param>
/// <param name="DedRating">1–10 where the description states one; null otherwise — never 0 and never "unknown".</param>
/// <param name="IsShipRestricted">
/// True when the site carries a ship allow-list. <paramref name="AllowedShipGroups"/> can still be empty then: a
/// handful of the underlying type lists express their restriction per hull rather than per group. Empty groups on a
/// restricted site therefore means "restricted, not expressible as groups" — not "all ships allowed".
/// </param>
/// <param name="AllowedShipGroups">
/// The ship groups the site allows in — an allow-list, not a maximum ship class. Empty when unrestricted.
/// </param>
public sealed record SdeSite(
    int DungeonId,
    string Name,
    int? ArchetypeId,
    string? ArchetypeName,
    int? FactionId,
    string? FactionName,
    string? Description,
    int? DedRating,
    bool IsShipRestricted,
    IReadOnlyList<SdeGroup> AllowedShipGroups);
