using System.Text.Json;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;

namespace EveUtils.Shared.Modules.Fleet;

/// <summary>
/// Registers the Fleet wire events so invite notifications travel over the remote bus. Registered
/// on both hosts: the server pushes these targeted events, the client deserializes the ones aimed at it.
/// </summary>
public sealed class FleetWireEvents : IWireEventCatalog
{
    public void RegisterInto(IEventTypeRegistry registry)
    {
        registry.Register("fleet.invite", (payloadJson, characterId) =>
        {
            var payload = JsonSerializer.Deserialize<FleetInvitePayload>(payloadJson)
                          ?? throw new InvalidOperationException("Invalid fleet.invite payload.");
            return new FleetInviteEvent(payload, characterId);
        });

        registry.Register("fleet.invite.responded", (payloadJson, characterId) =>
        {
            var payload = JsonSerializer.Deserialize<FleetInviteResponsePayload>(payloadJson)
                          ?? throw new InvalidOperationException("Invalid fleet.invite.responded payload.");
            return new FleetInviteRespondedEvent(payload, characterId);
        });

        // live activity samples shared with a fleet. Fleet-scoped → the server reroutes to the
        // fleet's active participants; both hosts deserialize it (server for the SignalR bridge + fleet-total,
        // a member's client for the live graph).
        registry.Register("fleet.metric", (payloadJson, characterId) =>
        {
            var payload = JsonSerializer.Deserialize<MetricSample>(payloadJson)
                          ?? throw new InvalidOperationException("Invalid fleet.metric payload.");
            return new FleetMetricEvent(payload, characterId);
        });

        // Fleet lifecycle/membership change pushed by the server to a fleet's members, so an open fleet list, roster
        // and the metrics participation refresh live instead of only on a reconnect/restart.
        registry.Register("fleet.changed", (payloadJson, characterId) =>
        {
            var payload = JsonSerializer.Deserialize<FleetChangePayload>(payloadJson)
                          ?? throw new InvalidOperationException("Invalid fleet.changed payload.");
            return new FleetChangedEvent(payload, characterId);
        });
    }
}
