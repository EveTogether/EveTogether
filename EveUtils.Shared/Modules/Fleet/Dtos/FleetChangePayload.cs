using EveUtils.Shared.Modules.Fleet.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>Payload of a fleet lifecycle/membership change pushed to a fleet's members so their open fleet list,
/// roster and metrics participation refresh live instead of only on a reconnect/restart.</summary>
public sealed record FleetChangePayload(long FleetId, FleetChangeKind Kind);
