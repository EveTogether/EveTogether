namespace EveUtils.Client.Fleet;

/// <summary>Client-side view of a fleet wing (gRPC <c>WingDto</c>), for the move-picker's wing choice-list.</summary>
public sealed record FleetWingInfo(
    long Id,
    long FleetId,
    string Name);
