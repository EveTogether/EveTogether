namespace EveUtils.Shared.Messaging;

/// <summary>Event with a strongly-typed payload. Subscribers can subscribe to this type or to the
/// concrete event type (dispatch matches on assignable type).</summary>
public interface IIntegrationEvent<out T> : IIntegrationEvent
{
    new T Data { get; }
}
