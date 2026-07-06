namespace EveUtils.Client.Fleet;

/// <summary>Client-side view of a composition fit-entry (gRPC <c>FleetCompositionEntryDto</c>).</summary>
public sealed record FleetCompositionEntryInfo(
    long Id,
    long RoleId,
    int? EntryMinCount,
    int SortOrder,
    FitReferenceInfo Fit);
