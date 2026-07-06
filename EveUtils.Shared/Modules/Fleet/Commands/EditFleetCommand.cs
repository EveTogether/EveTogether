using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Edits a fleet's editable details. Only the creator may edit it (enforced on <see cref="ActingCharacterId"/>
/// in the handler); the <c>fleet.edit</c> app-permission is gated server-side.
/// </summary>
[RequiresPermission(FleetPermissions.Edit)]
public sealed record EditFleetCommand(
    long FleetId,
    string Name,
    string? Description,
    FleetVisibility Visibility,
    DateTimeOffset? FromTime,
    DateTimeOffset? ToTime,
    FleetOfflineBehavior OfflineBehavior,
    int ActingCharacterId) : ICommand<Result>;
