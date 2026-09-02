namespace EveUtils.Server.Api.Dtos;

/// <summary>
/// A doctrine as a library row. <c>FleetCount</c> is how many fleets are coupled to it — the same "N fleets" the
/// Local API reports. Fetch its roles and fit-entries via <c>GET /api/v1/compositions/{id}</c>.
/// </summary>
public sealed record ApiCompositionListItem(
    long Id, string Name, string? Description, int OwnerCharacterId, int FleetCount);

/// <summary>A whole doctrine: its header plus the role-groups and their fit-entries, in sort order.</summary>
public sealed record ApiCompositionDetail(
    long Id, string Name, string? Description, int OwnerCharacterId, IReadOnlyList<ApiCompositionRole> Roles);

/// <summary>A role-group within a doctrine with its fit-entries and an optional group minimum.</summary>
public sealed record ApiCompositionRole(
    long Id, string RoleName, int? GroupMinCount, IReadOnlyList<ApiCompositionEntry> Entries);

/// <summary>
/// One fit-entry in a role: the ship type, the fit name and an optional per-fit minimum. The entry's stored fit
/// snapshot carries the raw fitting JSON and its content hash; neither is published here — the fit library is
/// M2's surface, and a doctrine only needs to say which fit it means.
/// </summary>
public sealed record ApiCompositionEntry(long Id, int? EntryMinCount, int ShipTypeId, string FitName);
