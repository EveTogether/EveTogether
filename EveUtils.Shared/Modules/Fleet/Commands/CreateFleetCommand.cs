using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Creates a fleet owned by <see cref="ActingCharacterId"/>. The
/// app-permission <c>fleet.create</c> is gated server-side; returns the new fleet's id.
/// </summary>
[RequiresPermission(FleetPermissions.Create)]
public sealed record CreateFleetCommand(
    string Name,
    string? Description,
    FleetVisibility Visibility,
    DateTimeOffset? FromTime,
    DateTimeOffset? ToTime,
    FleetOfflineBehavior OfflineBehavior,
    int ActingCharacterId) : ICommand<Result<long>>;
