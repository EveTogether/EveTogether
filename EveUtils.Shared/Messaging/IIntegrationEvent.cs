namespace EveUtils.Shared.Messaging;

/// <summary>
/// Shared base for ALL events — local and remote alike. The bus and the (future) external
/// transport layer work against this non-generic contract so they can handle, serialize and
/// route events heterogeneously; the payload is strongly typed in <see cref="IIntegrationEvent{T}.Data"/>.
/// </summary>
public interface IIntegrationEvent
{
    /// <summary>Unique id of this event instance (de-duplication / tracing across the wire).</summary>
    Guid EventId { get; }

    /// <summary>Source character; <c>null</c> for a system/host event (no character, e.g. a
    /// background watcher or a server-internal action).</summary>
    int? CharacterId { get; }

    /// <summary>Creation moment (UTC).</summary>
    DateTimeOffset Timestamp { get; }

    /// <summary>Stable, type-independent name used for routing/serialization over the external bus.</summary>
    string EventType { get; }

    /// <summary>Non-generic access to the payload (for the transport/serialization layer).</summary>
    object? Data { get; }
}
