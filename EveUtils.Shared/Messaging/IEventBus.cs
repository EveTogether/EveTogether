namespace EveUtils.Shared.Messaging;

/// <summary>
/// Event-bus contract. A single <see cref="PublishAsync"/> call chooses its destination via
/// <see cref="EventTarget"/>: <see cref="EventTarget.Local"/> (in-process),
/// <see cref="EventTarget.Remote"/> (external server↔client bus) or <see cref="EventTarget.Both"/>.
/// The current implementation (<see cref="InProcessEventBus"/>) delivers locally; remote is a
/// later, separate layer (<see cref="IRemoteEventTransport"/>) and is a no-op until it is wired.
/// </summary>
public interface IEventBus
{
    Task PublishAsync(
        IIntegrationEvent integrationEvent,
        EventTarget target = EventTarget.Local,
        CancellationToken cancellationToken = default);

    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IIntegrationEvent;
}
