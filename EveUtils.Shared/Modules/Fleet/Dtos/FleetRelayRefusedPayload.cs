using EveUtils.Shared.Modules.Fleet.Enums;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

public sealed record FleetRelayRefusedPayload(long FleetId, FleetRelayRefusal Reason);
