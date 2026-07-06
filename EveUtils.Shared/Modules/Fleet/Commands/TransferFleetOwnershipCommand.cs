using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Hands a fleet's ownership to another character. Only the current creator may do it (checked on
/// <see cref="ActingCharacterId"/> in the handler) and the new owner must already be a roster member; the
/// old owner stays on as a plain member. <c>fleet.edit</c> is gated server-side.
/// </summary>
[RequiresPermission(FleetPermissions.Edit)]
public sealed record TransferFleetOwnershipCommand(
    long FleetId, int NewOwnerCharacterId, int ActingCharacterId) : ICommand<Result>;
