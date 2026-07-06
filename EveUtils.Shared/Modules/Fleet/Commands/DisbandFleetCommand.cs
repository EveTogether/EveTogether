using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Disbands a fleet by soft-deleting it (<see cref="Entities.FleetState.Archived"/>); the cleanup
/// hard-deletes archived fleets later. Only the creator may disband it; <c>fleet.disband</c> is gated.
/// </summary>
[RequiresPermission(FleetPermissions.Disband)]
public sealed record DisbandFleetCommand(long FleetId, int ActingCharacterId) : ICommand<Result>;
