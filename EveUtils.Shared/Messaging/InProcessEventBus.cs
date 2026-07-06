using System.Collections.Concurrent;
using System.Reflection;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Identity;

namespace EveUtils.Shared.Messaging;

/// <summary>
/// Event-bus façade that picks the destination per publish (<see cref="EventTarget"/>). Delivers
/// the <see cref="EventTarget.Local"/> part itself: in-process, awaited synchronously to every
/// subscriber within the same app (UI ⇄ services ⇄ modules), without coupling the modules to each
/// other. Thread-safe; one shared singleton per host (client or server).
///
/// The <see cref="EventTarget.Remote"/> part is forwarded to an optional
/// <see cref="IRemoteEventTransport"/> (the later, separate external bus). If none is registered,
/// remote is a no-op — call sites need not know. Before forwarding, the foundation permission gate
/// (pillar 2) checks <see cref="RequiresPermissionAttribute"/> on the event type via the
/// optional <see cref="IAccessPolicy"/>; the local delivery is never gated.
/// </summary>
public sealed class InProcessEventBus(
    IRemoteEventTransport? remoteTransport = null,
    IAccessPolicy? accessPolicy = null,
    IPrincipalAccessor? principals = null) : IEventBus
{
    private static readonly ConcurrentDictionary<Type, string?> RemotePermissionCache = new();
    private readonly object _gate = new();

    // Subscriptions keyed by (possibly base/interface) type. Dispatch matches on IsAssignableFrom,
    // so you can also subscribe to a shared base or marker interface.
    private readonly List<Subscription> _subscriptions = [];

    public Task PublishAsync(
        IIntegrationEvent integrationEvent,
        EventTarget target = EventTarget.Local,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return PublishCoreAsync(integrationEvent, target, cancellationToken);
    }

    private async Task PublishCoreAsync(
        IIntegrationEvent integrationEvent, EventTarget target, CancellationToken cancellationToken)
    {
        var actualType = integrationEvent.GetType();
        List<Exception>? failures = null;

        if (target.HasFlag(EventTarget.Local))
            failures = await DeliverLocalAsync(integrationEvent, actualType, cancellationToken);

        if (target.HasFlag(EventTarget.Remote)
            && remoteTransport is not null
            && await IsRemoteAllowedAsync(integrationEvent, cancellationToken))
        {
            try
            {
                await remoteTransport.SendAsync(integrationEvent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                (failures ??= []).Add(ex);
            }
        }
        // target.Remote without a transport = deliberate no-op (external bus not wired yet).
        // A denied remote event is deliberately not forwarded either (authorization decision, not an
        // error); under the v1 OwnerAllPolicy this always passes.

        if (failures is { Count: > 0 })
            throw new AggregateException(
                $"{failures.Count} receiver(s) failed while handling {actualType.Name}.", failures);
    }

    private Task<bool> IsRemoteAllowedAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        if (accessPolicy is null || principals is null)
            return Task.FromResult(true);

        var code = RemotePermissionCache.GetOrAdd(
            integrationEvent.GetType(),
            static t => t.GetCustomAttribute<RequiresPermissionAttribute>()?.Code);

        return code is null
            ? Task.FromResult(true)
            : accessPolicy.IsAllowedAsync(principals.Current, code, cancellationToken);
    }

    private async Task<List<Exception>?> DeliverLocalAsync(
        IIntegrationEvent integrationEvent, Type actualType, CancellationToken cancellationToken)
    {
        Subscription[] matches;
        lock (_gate)
        {
            // Snapshot under lock: handlers may (un)subscribe during publish without breaking iteration.
            matches = _subscriptions
                .Where(s => s.EventType.IsAssignableFrom(actualType))
                .ToArray();
        }

        // Sequential + deterministic (a UI bus wants predictable ordering). No "silent" failures
        // a failing subscriber does not stop the others, but it is reported.
        List<Exception>? failures = null;
        foreach (var subscription in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await subscription.Invoke(integrationEvent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                (failures ??= []).Add(ex);
            }
        }

        return failures;
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
        where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(
            typeof(TEvent),
            (evt, ct) => handler((TEvent)evt, ct));

        lock (_gate)
            _subscriptions.Add(subscription);

        return new Unsubscriber(this, subscription);
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
            _subscriptions.Remove(subscription);
    }

    private sealed record Subscription(Type EventType, Func<IIntegrationEvent, CancellationToken, Task> Invoke);

    private sealed class Unsubscriber(InProcessEventBus bus, Subscription subscription) : IDisposable
    {
        private InProcessEventBus? _bus = bus;

        public void Dispose()
        {
            _bus?.Remove(subscription);
            _bus = null; // idempotent: double-dispose is safe.
        }
    }
}
