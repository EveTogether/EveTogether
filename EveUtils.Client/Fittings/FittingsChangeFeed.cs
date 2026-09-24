using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EveUtils.Client.Messaging;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fittings.Events;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Fittings;

/// <summary>
/// The one place a screen listing the local fit library hears that a fit was stored (ET-382), whether by its own
/// import, a clipboard offer or another window. The window, the UI thread and the one-reload-at-a-time rule are
/// <see cref="ChangeFeed{TEvent}"/>'s.
/// </summary>
public sealed class FittingsChangeFeed(IEventBus eventBus, ILogger<FittingsChangeFeed> logger, TimeSpan window)
    : ISingletonService, IDisposable
{
    private readonly ChangeFeed<FittingsChangedEvent> _feed = new(eventBus, logger, window);

    public FittingsChangeFeed(IEventBus eventBus, ILogger<FittingsChangeFeed> logger)
        : this(eventBus, logger, ChangeFeed<FittingsChangedEvent>.DefaultWindow)
    {
    }

    /// <summary>Calls <paramref name="reload"/> on the UI thread with each batch of fit changes, in the order they were
    /// published, until the returned handle is disposed.</summary>
    public IDisposable Subscribe(Func<IReadOnlyList<FittingsChangedEvent>, Task> reload) => _feed.Subscribe(reload);

    public void Dispose() => _feed.Dispose();
}
