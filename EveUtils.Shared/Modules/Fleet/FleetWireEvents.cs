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

        registry.Register("fleet.run-group", (payloadJson, characterId) =>
        {
            var payload = JsonSerializer.Deserialize<RunGroupCodeStart>(payloadJson)
                          ?? throw new InvalidOperationException("Invalid fleet.run-group payload.");
            return new FleetRunGroupCodeEvent(payload, characterId);
        });

        // The commander set an abyssal up before anyone went in (ET-246): the start's own payload, offered early.
        registry.Register("fleet.run-group.prepared", (payloadJson, characterId) =>
        {
            var payload = JsonSerializer.Deserialize<RunGroupCodeStart>(payloadJson)
                          ?? throw new InvalidOperationException("Invalid fleet.run-group.prepared payload.");
            return new FleetRunGroupPreparedEvent(payload, characterId);
        });

        registry.Register("fleet.run-stopped", (payloadJson, characterId) =>
        {
            var payload = JsonSerializer.Deserialize<RunGroupStop>(payloadJson)
                          ?? throw new InvalidOperationException("Invalid fleet.run-stopped payload.");
            return new FleetRunStoppedEvent(payload, characterId);
        });

        // One pilot's own exit from a run whose clock is per pilot (ET-243) — stops nobody else.
        registry.Register("fleet.run-group.pilot-stopped", (payloadJson, characterId) =>
        {
            var payload = JsonSerializer.Deserialize<RunGroupStop>(payloadJson)
                          ?? throw new InvalidOperationException("Invalid fleet.run-group.pilot-stopped payload.");
            return new FleetRunPilotStoppedEvent(payload, characterId);
        });

        // One pilot's own leg started again after their own STOP (ET-250) — stops nobody and starts nobody else.
        registry.Register("fleet.run-group.pilot-resumed", (payloadJson, characterId) =>
        {
            var payload = JsonSerializer.Deserialize<RunGroupResume>(payloadJson)
                          ?? throw new InvalidOperationException("Invalid fleet.run-group.pilot-resumed payload.");
            return new FleetRunPilotResumedEvent(payload, characterId);
        });

        // The commander changed the pocket's tier or weather mid-run (ET-241); a member already on the group code
        // adopts it the same way it adopted the original announcement.
        registry.Register("fleet.run-group.abyssal-updated", (payloadJson, characterId) =>
        {
            var payload = JsonSerializer.Deserialize<RunGroupAbyssalUpdate>(payloadJson)
                          ?? throw new InvalidOperationException("Invalid fleet.run-group.abyssal-updated payload.");
            return new FleetRunGroupAbyssalUpdatedEvent(payload, characterId);
        });

        registry.Register("fleet.run-discarded", (payloadJson, characterId) =>
        {
            var payload = JsonSerializer.Deserialize<RunGroupDiscard>(payloadJson)
                          ?? throw new InvalidOperationException("Invalid fleet.run-discarded payload.");
            return new FleetRunDiscardedEvent(payload, characterId);
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
