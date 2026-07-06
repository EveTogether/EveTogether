using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Starts a fleet: flips its <see cref="Entities.FleetActivation"/> from
/// <see cref="Entities.FleetActivation.Forming"/> to <see cref="Entities.FleetActivation.Active"/> and notifies
/// every roster member (bar the creator and externals) that it has started. Only the creator may start it
/// (enforced on <see cref="ActingCharacterId"/> in the handler); <c>fleet.edit</c> is gated server-side.
/// Idempotent: starting an already-Active fleet succeeds without re-notifying.
/// </summary>
[RequiresPermission(FleetPermissions.Edit)]
public sealed record StartFleetCommand(long FleetId, int ActingCharacterId) : ICommand<Result>;
