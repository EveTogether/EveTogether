using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Adds a wing to a fleet. Only the fleet's creator may manage its structure (checked
/// on <see cref="ActingCharacterId"/> in the handler); <c>fleet.structure</c> is gated server-side.
/// Returns the new wing's id.
/// </summary>
[RequiresPermission(FleetPermissions.Structure)]
public sealed record CreateWingCommand(long FleetId, string Name, int ActingCharacterId) : ICommand<Result<long>>;
