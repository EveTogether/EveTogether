using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Fittings.Events;

/// <summary>
/// Published when a client shares a fitting to the server.
/// Route: client → EventTarget.Both → server event-bus gate checks fit.sync → stores + re-routes.
/// The <see cref="RequiresPermissionAttribute"/> is enforced by the server event-bus gate.
/// </summary>
[RequiresPermission(FittingsPermissions.Sync)]
public sealed class FitSharedEvent(FitSharedPayload data, int? characterId = null)
    : IntegrationEvent<FitSharedPayload>(data, characterId)
{
    public override string EventType => "fittings.shared";
}
