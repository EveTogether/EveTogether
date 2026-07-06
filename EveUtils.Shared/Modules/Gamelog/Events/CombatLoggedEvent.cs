using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Dtos;

namespace EveUtils.Shared.Modules.Gamelog.Events;

/// <summary>
/// A live DPS sample for one character. Travels over the wire (event bus), so it declares a
/// stable <see cref="EventType"/> and the permission its remote delivery requires: the
/// event-bus gate checks <c>gamelog.stream</c> before forwarding to the remote transport. Local UI
/// delivery is never gated.
/// </summary>
[RequiresPermission(GamelogPermissions.Stream)]
public sealed class CombatLoggedEvent(DpsSampleDto data, int? characterId = null)
    : IntegrationEvent<DpsSampleDto>(data, characterId)
{
    public override string EventType => "gamelog.combat";
}
