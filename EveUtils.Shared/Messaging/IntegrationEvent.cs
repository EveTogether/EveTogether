namespace EveUtils.Shared.Messaging;

/// <summary>
/// Base class for all events (local and remote alike). Fills in the shared metadata (id,
/// timestamp, type name) and carries a strongly-typed <typeparamref name="T"/> payload. Derive
/// per concrete event:
/// <code>
/// public sealed class FitAddedEvent(FitDto data, int? characterId)
///     : IntegrationEvent&lt;FitDto&gt;(data, characterId);
/// </code>
/// </summary>
public abstract class IntegrationEvent<T> : IIntegrationEvent<T>
{
    protected IntegrationEvent(T data, int? characterId = null)
    {
        Data = data;
        CharacterId = characterId;
    }

    public Guid EventId { get; init; } = Guid.NewGuid();

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public int? CharacterId { get; init; }

    // init rather than set: a published event is delivered to multiple subscribers and will
    // travel over the wire — mutating it mid-delivery is a footgun. Build a new event instead.
    public T Data { get; init; }

    /// <summary>Defaults to the concrete type name; override for a custom stable wire name.</summary>
    public virtual string EventType => GetType().Name;

    // Non-generic access for the transport/serialization layer.
    object? IIntegrationEvent.Data => Data;
}
