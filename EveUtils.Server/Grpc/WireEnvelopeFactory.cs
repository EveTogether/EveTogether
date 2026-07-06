using System.Text.Json;
using EveUtils.Grpc;
using EveUtils.Shared.Messaging;

namespace EveUtils.Server.Grpc;

/// <summary>
/// Builds a wire <see cref="EventEnvelope"/> from a server-originated <see cref="IIntegrationEvent"/> so the
/// server can push events to connected clients (mirrors the client's outbound ToEnvelope). Targeted events
/// carry their recipient so <see cref="ConnectedClients.SendToCharacterAsync"/> reaches only that
/// character's connections.
/// </summary>
public static class WireEnvelopeFactory
{
    public static EventEnvelope ToEnvelope(IIntegrationEvent integrationEvent)
    {
        var envelope = new EventEnvelope
        {
            EventType = integrationEvent.EventType,
            EventId = integrationEvent.EventId.ToString(),
            CharacterId = integrationEvent.CharacterId ?? 0,
            Timestamp = integrationEvent.Timestamp.ToString("o"),
            PayloadJson = integrationEvent.Data is null
                ? "{}"
                : JsonSerializer.Serialize(integrationEvent.Data, integrationEvent.Data.GetType())
        };

        if (integrationEvent is ITargetedEvent targeted)
            envelope.TargetCharacterId = targeted.TargetCharacterId;

        if (integrationEvent is IFleetScopedEvent fleetScoped)
            envelope.FleetId = fleetScoped.FleetId;

        return envelope;
    }
}
