using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Shared.Modules.Fleet.Commands;

/// <summary>
/// Creates a fleet owned by <see cref="ActingCharacterId"/>. The
/// app-permission <c>fleet.create</c> is gated server-side; returns the new fleet's id.
/// <see cref="IsClientOnly"/> marks a fleet that lives only in this client's own database and is never published;
/// only the client's local fleet flow sets it.
/// </summary>
[RequiresPermission(FleetPermissions.Create)]
public sealed record CreateFleetCommand(
    string Name,
    string? Description,
    FleetVisibility Visibility,
    DateTimeOffset? FromTime,
    DateTimeOffset? ToTime,
    FleetOfflineBehavior OfflineBehavior,
    int ActingCharacterId,
    bool IsClientOnly = false) : ICommand<Result<long>>;
