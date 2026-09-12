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
/// <param name="GameplayDescription">
/// A second, separate text from the SDE (ET-232) — recommended fleet size, expected time, roles — distinct from
/// <paramref name="Description"/>, which is CCP's own flavour text. Null when the site carries none (most sites).
/// </param>
/// <param name="IncludedShipTypes">
/// Individual ship types the site allows beyond <paramref name="AllowedShipGroups"/> (ET-232) — the refinement a
/// group alone cannot express, e.g. a homefront's T1-only cruisers are 16 named types, not the whole Cruiser group.
/// Empty when the site's allow-list needs no individual hulls, which is most restricted sites.
/// </param>
/// <param name="ExcludedShipTypes">
/// Individual ship types explicitly turned away even where <paramref name="AllowedShipGroups"/> or
/// <paramref name="IncludedShipTypes"/> would otherwise let them in (ET-232) — never shown as allowed. Empty when
/// the site excludes no individual hull.
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
    IReadOnlyList<SdeGroup> AllowedShipGroups,
    string? GameplayDescription = null,
    IReadOnlyList<SdeNamedType>? IncludedShipTypes = null,
    IReadOnlyList<SdeNamedType>? ExcludedShipTypes = null)
{
    public IReadOnlyList<SdeNamedType> IncludedShipTypes { get; init; } = IncludedShipTypes ?? [];
    public IReadOnlyList<SdeNamedType> ExcludedShipTypes { get; init; } = ExcludedShipTypes ?? [];
}
