namespace EveUtils.Client.Fleet;

/// <summary>Client-side view of a fit snapshot in a composition (gRPC <c>FitReferenceDto</c>).</summary>
public sealed record FitReferenceInfo(
    int ShipTypeId,
    string FitName,
    string RawJson,
    string ContentHash,
    int? LocalFittingId,
    int? ServerSharedFitId);
