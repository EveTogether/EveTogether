using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EveUtils.Client.Messaging;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Events;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Killmails;

/// <summary>
/// The one place a screen showing killmails hears that a loss was linked to a run or unlinked from one (ET-382),
/// whichever window made the change. The window, the UI thread and the one-reload-at-a-time rule are
/// <see cref="ChangeFeed{TEvent}"/>'s.
/// </summary>
public sealed class KillmailsChangeFeed(IEventBus eventBus, ILogger<KillmailsChangeFeed> logger, TimeSpan window)
    : ISingletonService, IDisposable
{
    private readonly ChangeFeed<KillmailsChangedEvent> _feed = new(eventBus, logger, window);

    public KillmailsChangeFeed(IEventBus eventBus, ILogger<KillmailsChangeFeed> logger)
        : this(eventBus, logger, ChangeFeed<KillmailsChangedEvent>.DefaultWindow)
    {
    }

    /// <summary>Calls <paramref name="reload"/> on the UI thread with each batch of killmail changes, in the order they
    /// were published, until the returned handle is disposed.</summary>
    public IDisposable Subscribe(Func<IReadOnlyList<KillmailsChangedEvent>, Task> reload) => _feed.Subscribe(reload);

    public void Dispose() => _feed.Dispose();
}
