namespace EveUtils.Client.Fleet;

/// <summary>Client-side view of a squad (gRPC <c>SquadDto</c>), for the move-picker's squad choice-list.</summary>
public sealed record FleetSquadInfo(
    long Id,
    long WingId,
    string Name);
