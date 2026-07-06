using System.Collections.Concurrent;

namespace EveUtils.Shared.Messaging.Wire;

public sealed class EventTypeRegistry : IEventTypeRegistry
{
    private readonly ConcurrentDictionary<string, Func<string, int?, IIntegrationEvent>> _deserializers =
        new(StringComparer.Ordinal);

    public void Register(string eventType, Func<string, int?, IIntegrationEvent> deserializer) =>
        _deserializers[eventType] = deserializer;

    public IIntegrationEvent? Deserialize(string eventType, string payloadJson, int? characterId) =>
        _deserializers.TryGetValue(eventType, out var deserializer)
            ? deserializer(payloadJson, characterId)
            : null;
}
