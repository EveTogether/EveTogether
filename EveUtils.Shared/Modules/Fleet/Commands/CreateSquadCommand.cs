using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Adds a squad to a wing. Creator-only via the wing's owning fleet;
/// <c>fleet.structure</c> gated. Returns the new squad's id.
/// </summary>
[RequiresPermission(FleetPermissions.Structure)]
public sealed record CreateSquadCommand(long WingId, string Name, int ActingCharacterId) : ICommand<Result<long>>;
