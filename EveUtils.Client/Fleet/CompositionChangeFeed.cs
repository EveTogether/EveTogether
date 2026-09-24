using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EveUtils.Client.Messaging;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Events;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Fleet;

/// <summary>
/// The one place a screen showing compositions hears that one changed (ET-381). Both origins arrive as the same
/// <see cref="CompositionChangedEvent"/>: a change to the local library is published by its own handler, a change on a
/// server is pushed back by that server to every client — this one included when it made the change, so it never
/// announces a server change itself. The window, the UI thread and the one-reload-at-a-time rule are
/// <see cref="ChangeFeed{TEvent}"/>'s.
/// </summary>
public sealed class CompositionChangeFeed(IEventBus eventBus, ILogger<CompositionChangeFeed> logger, TimeSpan window)
    : ISingletonService, IDisposable
{
    private readonly ChangeFeed<CompositionChangedEvent> _feed = new(eventBus, logger, window);

    public CompositionChangeFeed(IEventBus eventBus, ILogger<CompositionChangeFeed> logger)
        : this(eventBus, logger, ChangeFeed<CompositionChangedEvent>.DefaultWindow)
    {
    }

    /// <summary>Calls <paramref name="reload"/> on the UI thread with each batch of composition changes, in the order
    /// they were published, until the returned handle is disposed.</summary>
    public IDisposable Subscribe(Func<IReadOnlyList<CompositionChangedEvent>, Task> reload) => _feed.Subscribe(reload);

    public void Dispose() => _feed.Dispose();
}
