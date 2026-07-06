using System.Collections.Generic;

namespace EveUtils.Client.Fleet;

/// <summary>Client-side view of a composition role-group with its fit-entries (gRPC <c>FleetCompositionRoleDto</c>).</summary>
public sealed record FleetCompositionRoleInfo(
    long Id,
    long CompositionId,
    string RoleName,
    int? GroupMinCount,
    int SortOrder,
    IReadOnlyList<FleetCompositionEntryInfo> Entries);
