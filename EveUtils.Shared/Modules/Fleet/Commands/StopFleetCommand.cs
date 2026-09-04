using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Stops a fleet (ET-166): flips its <see cref="Entities.FleetActivation"/> from
/// <see cref="Entities.FleetActivation.Active"/> back to <see cref="Entities.FleetActivation.Forming"/> — the way
/// back that <see cref="ConcludeFleetCommand"/> deliberately is not. The fleet keeps its roster, its name and its
/// coupled doctrine, so a weekly op is started again next week instead of being recreated. The members stay on the
/// roster; only their coupling lapses, because "coupled" is derived from the fleet being Active
/// (<c>IFleetRepository.ListActiveMembershipsAsync</c>) — so they are free to be coupled to another fleet the moment
/// this one stops. Only the creator may stop it (enforced on <see cref="ActingCharacterId"/> in the handler);
/// <c>fleet.edit</c> is gated server-side. Idempotent on an already-Forming fleet; refused on a Concluded one, which
/// is terminal.
/// </summary>
[RequiresPermission(FleetPermissions.Edit)]
public sealed record StopFleetCommand(long FleetId, int ActingCharacterId) : ICommand<Result>;
