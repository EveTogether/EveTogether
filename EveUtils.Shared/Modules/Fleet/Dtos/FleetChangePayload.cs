using EveUtils.Shared.Modules.Fleet.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>Payload of a fleet lifecycle/membership change pushed to a fleet's members so their open fleet list,
/// roster and metrics participation refresh live instead of only on a reconnect/restart.</summary>
/// <param name="StopTrigger">Why the fleet stopped, on a <see cref="FleetChangeKind.Stopped"/> change; null on every
/// other kind. Optional on the wire on purpose — this payload is JSON between a client and a server that update
/// separately, so a server that predates ET-167 simply omits it and an older client ignores it.</param>
public sealed record FleetChangePayload(
    long FleetId,
    FleetChangeKind Kind,
    FleetStopTrigger? StopTrigger = null);
