using EveUtils.Client.Messaging;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Runs;

/// <summary>
/// The one place a screen that shows runs hears that one changed (ET-222). Until this existed every screen subscribed
/// to a hand-picked set of specific run events and knew per event which part to reload, and every new command or
/// screen was one more pairing to remember — ET-189, ET-203 and ET-220 were each one of them forgotten.
///
/// <para>The window, the UI thread and the one-reload-at-a-time rule are <see cref="ChangeFeed{TEvent}"/>'s; this
/// folds each batch of <see cref="RunsChangedEvent"/>s into the <see cref="RunChangeBatch"/> a runs screen asks.</para>
/// </summary>
public sealed class RunChangeFeed(IEventBus eventBus, ILogger<RunChangeFeed> logger, TimeSpan window)
    : ISingletonService, IDisposable
{
    public static readonly TimeSpan DefaultWindow = ChangeFeed<RunsChangedEvent>.DefaultWindow;

    private readonly ChangeFeed<RunsChangedEvent> _feed = new(eventBus, logger, window);

    public RunChangeFeed(IEventBus eventBus, ILogger<RunChangeFeed> logger) : this(eventBus, logger, DefaultWindow)
    {
    }

    /// <summary>Calls <paramref name="reload"/> on the UI thread with each batch of run changes, until the returned
    /// handle is disposed.</summary>
    public IDisposable Subscribe(Func<RunChangeBatch, Task> reload) =>
        _feed.Subscribe(changes => reload(RunChangeBatch.Of(changes)));

    /// <summary>A change to what a screen shows about a group that is not a write — where its publish stands (ET-245).
    /// Not on the bus: nothing about the run itself changed.</summary>
    public void Announce(string groupCode) => _feed.Announce(new RunsChangedEvent(null, groupCode));

    /// <summary>The same for one run, e.g. where its EVE Workbench upload stands (ET-325); also not on the bus, so the
    /// publisher that announces it is not woken by its own news.</summary>
    public void Announce(Guid runId, string? groupCode) => _feed.Announce(new RunsChangedEvent(runId, groupCode));

    public void Dispose() => _feed.Dispose();
}
