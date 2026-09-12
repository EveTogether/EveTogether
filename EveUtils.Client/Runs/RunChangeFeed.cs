using Avalonia.Threading;
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
/// <para><b>Why a window.</b> Bounty lines and loot captures arrive in bursts while a run is flown, and each is its
/// own write. The first <see cref="RunsChangedEvent"/> opens a window of <see cref="DefaultWindow"/>; everything that
/// arrives inside it is folded into one <see cref="RunChangeBatch"/>, delivered once when it closes. Fixed from the
/// first signal rather than restarted by every later one: a window that restarts never closes while payouts keep
/// coming, which is exactly when the pilot is watching the figures move.</para>
///
/// <para><b>Why here.</b> A command publishes on whatever thread it ran on — server synchronisation included — and a
/// listener here is always called on the UI thread, one reload at a time: a batch that arrives while a listener is
/// still reading is kept for it and handed over the moment it finishes, so two reads never interleave on one screen.</para>
/// </summary>
public sealed class RunChangeFeed(IEventBus eventBus, ILogger<RunChangeFeed> logger, TimeSpan window)
    : ISingletonService, IDisposable
{
    /// <summary>Short enough to read as immediate after a click, long enough that a pocket's worth of payouts is one
    /// reload rather than one per line.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly List<Listener> _listeners = [];
    private RunChangeBatch? _pending;
    private IDisposable? _subscription;

    public RunChangeFeed(IEventBus eventBus, ILogger<RunChangeFeed> logger) : this(eventBus, logger, DefaultWindow)
    {
    }

    /// <summary>Calls <paramref name="reload"/> on the UI thread with each batch of run changes, until the returned
    /// handle is disposed.</summary>
    public IDisposable Subscribe(Func<RunChangeBatch, Task> reload)
    {
        var listener = new Listener(reload, logger);
        lock (_gate)
        {
            // Subscribed on first use rather than at construction: a feed nobody listens to has no reason to hear
            // every bounty line.
            _subscription ??= eventBus.Subscribe<RunsChangedEvent>(_OnRunsChanged);
            _listeners.Add(listener);
        }

        return new Unsubscriber(this, listener);
    }

    /// <summary>A change to what a screen shows about a group that is not a write — where its publish stands (ET-245).
    /// Handed over through the same window and one-reload-at-a-time rule as a stored change, rather than on a second
    /// path whose reload could interleave with this one's. Not on the bus: nothing about the run itself changed.</summary>
    public void Announce(string groupCode) => _OnRunsChanged(new RunsChangedEvent(null, groupCode));

    private void _OnRunsChanged(RunsChangedEvent changed)
    {
        bool opensWindow;
        lock (_gate)
        {
            opensWindow = _pending is null;
            (_pending ??= new RunChangeBatch()).Add(changed.Data);
        }

        if (!opensWindow)
            return;

        if (window <= TimeSpan.Zero)
            Dispatcher.UIThread.Post(_Deliver);
        else
            _ = _DeliverAfterWindowAsync();
    }

    private async Task _DeliverAfterWindowAsync()
    {
        await Task.Delay(window).ConfigureAwait(false);
        Dispatcher.UIThread.Post(_Deliver);
    }

    private void _Deliver()
    {
        RunChangeBatch? batch;
        Listener[] listeners;
        lock (_gate)
        {
            batch = _pending;
            _pending = null;
            listeners = [.. _listeners];
        }

        if (batch is null)
            return;

        foreach (Listener listener in listeners)
            listener.Deliver(batch);
    }

    private void _Remove(Listener listener)
    {
        listener.IsRemoved = true;
        lock (_gate)
            _listeners.Remove(listener);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _subscription?.Dispose();
            _subscription = null;
            _listeners.Clear();
        }
    }

    /// <summary>One screen's reload, serialised: only ever touched on the UI thread, so no lock of its own.</summary>
    private sealed class Listener(Func<RunChangeBatch, Task> reload, ILogger logger)
    {
        private RunChangeBatch? _queued;
        private bool _isReloading;

        public bool IsRemoved { get; set; }

        public void Deliver(RunChangeBatch batch)
        {
            if (_isReloading)
            {
                (_queued ??= new RunChangeBatch()).Add(batch);
                return;
            }

            _ = _ReloadAsync(batch);
        }

        private async Task _ReloadAsync(RunChangeBatch batch)
        {
            _isReloading = true;
            try
            {
                for (RunChangeBatch? next = batch; next is not null && !IsRemoved; next = _TakeQueued())
                {
                    try
                    {
                        await reload(next);
                    }
                    catch (Exception exception)
                    {
                        // A screen that failed one read must still take the next batch, or it would stay stale for the
                        // rest of the session without anyone being told.
                        logger.LogError(exception, "A screen could not read its runs again after they changed");
                    }
                }
            }
            finally
            {
                _isReloading = false;
            }
        }

        private RunChangeBatch? _TakeQueued()
        {
            RunChangeBatch? queued = _queued;
            _queued = null;
            return queued;
        }
    }

    private sealed class Unsubscriber(RunChangeFeed feed, Listener listener) : IDisposable
    {
        private RunChangeFeed? _feed = feed;

        public void Dispose()
        {
            _feed?._Remove(listener);
            _feed = null;
        }
    }
}
