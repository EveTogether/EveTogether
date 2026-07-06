namespace EveUtils.Shared.Messaging;

public static class EventBusExtensions
{
    /// <summary>
    /// Ergonomic synchronous listener — for UI handlers that need not return a <see cref="Task"/>
    /// (e.g. window B refreshing its datagrid when window A adds a fit). Returns an
    /// <see cref="IDisposable"/> to unsubscribe, just like <see cref="IEventBus.Subscribe{TEvent}"/>.
    /// </summary>
    public static IDisposable Subscribe<TEvent>(this IEventBus eventBus, Action<TEvent> handler)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(eventBus);
        ArgumentNullException.ThrowIfNull(handler);

        return eventBus.Subscribe<TEvent>((evt, _) =>
        {
            handler(evt);
            return Task.CompletedTask;
        });
    }
}
