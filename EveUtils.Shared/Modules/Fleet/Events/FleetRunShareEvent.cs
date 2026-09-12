using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

/// <summary>
/// One pilot's share of their own fleet run (ET-242): the loot that counts on it, and whether their loot and bounty are
/// offered at all. Fleet-scoped like <see cref="FleetMetricEvent"/>, and behind the same <c>fleet.metrics</c>
/// app-permission — the operator who switches metric sharing off server-wide switches this off with it.
///
/// A client or server that predates it has no deserializer for its type and drops it unread, which leaves such a
/// member exactly where they were: the ISK figure on the metric stream, and no items.
/// </summary>
[RequiresPermission(FleetPermissions.Metrics)]
public sealed class FleetRunShareEvent(RunShareUpdate data, int? characterId = null)
    : IntegrationEvent<RunShareUpdate>(data, characterId), IFleetScopedEvent
{
    public override string EventType => "fleet.run-share";

    public long FleetId => Data.FleetId;
}
