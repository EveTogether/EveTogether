using System;

namespace EveUtils.Client.Fleet;

/// <summary>Client-side header view of a composition (gRPC <c>FleetCompositionDto</c>), for the library list.
/// <paramref name="CanEdit"/> is the server's owner-or-manage verdict for the acting character (always true for the
/// local library); <paramref name="OwnerName"/> is the resolved owner name for compositions owned by other characters;
/// <paramref name="FleetCount"/> is how many fleets are coupled to this composition (the "N fleets" pill).</summary>
public sealed record FleetCompositionInfo(
    long Id,
    string Name,
    string? Description,
    int OwnerCharacterId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool CanEdit = true,
    string OwnerName = "",
    int FleetCount = 0);
