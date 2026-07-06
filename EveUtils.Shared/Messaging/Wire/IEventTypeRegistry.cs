namespace EveUtils.Shared.Messaging.Wire;

/// <summary>
/// Maps a stable <see cref="IIntegrationEvent.EventType"/> to a deserializer, so a wire envelope
/// (event type + JSON payload) can be reconstructed into the right strongly-typed event on either
/// host. Populated at startup from the modules' <see cref="IWireEventCatalog"/>s.
/// </summary>
public interface IEventTypeRegistry
{
    void Register(string eventType, Func<string, int?, IIntegrationEvent> deserializer);

    IIntegrationEvent? Deserialize(string eventType, string payloadJson, int? characterId);
}
