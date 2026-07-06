using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Shared.Modules.Fleet.Events;

/// <summary>
/// A live activity sample shared with one fleet. Fleet-scoped (<see cref="IFleetScopedEvent"/>): the
/// transport copies <see cref="FleetId"/> onto the wire envelope and the server reroutes it only to that fleet's
/// active participants, the sender excluded. Travels over the remote bus, so its remote delivery is gated
/// by the <c>fleet.metrics</c> app-permission — the operator can switch off metric sharing
/// server-wide; local UI delivery is never gated.
/// </summary>
[RequiresPermission(FleetPermissions.Metrics)]
public sealed class FleetMetricEvent(MetricSample data, int? characterId = null)
    : IntegrationEvent<MetricSample>(data, characterId), IFleetScopedEvent
{
    public override string EventType => "fleet.metric";

    public long FleetId => Data.FleetId;
}
