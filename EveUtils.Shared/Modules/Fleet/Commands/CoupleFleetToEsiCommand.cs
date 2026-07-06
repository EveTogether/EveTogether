using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Links an internal fleet to the live in-game ESI fleet. The owner couples after forming the fleet
/// in-game and detecting its <c>fleet_id</c> via <c>GET /characters/{id}/fleet/</c>; this persists the ESI-parity
/// naden and marks the fleet <see cref="Entities.EsiFleetSyncState.Linked"/>. One internal fleet ↔ one in-game fleet
/// (Q4, 2026-06-14). Owner-only, <c>fleet.edit</c> gated.
/// </summary>
[RequiresPermission(FleetPermissions.Edit)]
public sealed record CoupleFleetToEsiCommand(
    long FleetId, long EsiFleetId, int EsiFleetBossId, int ActingCharacterId) : ICommand<Result>;
