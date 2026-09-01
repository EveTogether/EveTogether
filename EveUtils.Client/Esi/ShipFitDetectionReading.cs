namespace EveUtils.Client.Esi;

/// <summary>Read-only current-ship cache value. <see cref="State"/> never conflates unread, missing-scope and observed results.</summary>
public sealed record ShipFitDetectionReading(
    ShipFitDetectionState State,
    DateTimeOffset? ObservedAtUtc,
    int? ShipTypeId,
    long? ShipItemId,
    string? ShipName,
    ShipFitCandidate? SelectedFit,
    ShipFitMatchReason? MatchReason,
    IReadOnlyList<ShipFitCandidate> Candidates)
{
    public static ShipFitDetectionReading Unobserved { get; } = new(
        ShipFitDetectionState.Unobserved, null, null, null, null, null, null, []);

    public static ShipFitDetectionReading ScopeMissing { get; } = new(
        ShipFitDetectionState.ScopeMissing, null, null, null, null, null, null, []);
}
