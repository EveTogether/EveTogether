using System.Collections.Generic;

namespace EveUtils.Client.Fleet;

/// <summary>Client-side view of a whole composition — header + role-groups + their fit-entries (gRPC
/// <c>FleetCompositionViewDto</c>), for the composition editor.</summary>
public sealed record FleetCompositionDetail(
    FleetCompositionInfo Composition,
    IReadOnlyList<FleetCompositionRoleInfo> Roles);
