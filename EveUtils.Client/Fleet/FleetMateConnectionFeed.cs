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
/// The one place a fleet screen hears that a fleet mate connected to a server or went away (ET-492). The roster holds the
/// server's connection flag only as it stood when it was read, so a screen re-reads its roster on this. The window, the
/// UI thread and the one-reload-at-a-time rule are <see cref="ChangeFeed{TEvent}"/>'s.
/// </summary>
public sealed class FleetMateConnectionFeed(IEventBus eventBus, ILogger<FleetMateConnectionFeed> logger, TimeSpan window)
    : ISingletonService, IDisposable
{
    private readonly ChangeFeed<FleetMateConnectionEvent> _feed = new(eventBus, logger, window);

    public FleetMateConnectionFeed(IEventBus eventBus, ILogger<FleetMateConnectionFeed> logger)
        : this(eventBus, logger, ChangeFeed<FleetMateConnectionEvent>.DefaultWindow)
    {
    }

    /// <summary>Calls <paramref name="reload"/> on the UI thread with each batch of connection changes until the returned
    /// handle is disposed.</summary>
    public IDisposable Subscribe(Func<IReadOnlyList<FleetMateConnectionEvent>, Task> reload) => _feed.Subscribe(reload);

    public void Dispose() => _feed.Dispose();
}
