using Avalonia.Threading;
using EveUtils.Shared.Messaging;
using Microsoft.Extensions.Logging;

namespace EveUtils.Client.Messaging;

/// <summary>
/// The one place a screen hears that a module's state changed (ET-222, generalised by ET-380). A command publishes its
/// module signal <typeparamref name="TEvent"/> on the bus; a screen subscribes here, never on the bus itself.
///
/// <para><b>Why not straight on the bus.</b> <see cref="InProcessEventBus"/> awaits every subscriber inside the
/// publishing command, after its write is committed: a subscriber that reloads inline holds the command up, and one
/// that throws reports a failure for a change that was in fact saved. The subscription here only records the change and
/// returns; the work happens later, on the UI thread.</para>
///
/// <para><b>Why a window.</b> Changes arrive in bursts — a pocket's worth of bounty lines is one write each. The first
/// signal opens a window; everything inside it is handed over as one batch when it closes. Fixed from the first signal
/// rather than restarted by every later one: a window that restarts never closes while changes keep coming.</para>
///
/// <para><b>Why serialised.</b> A command publishes on whatever thread it ran on. A listener here is always called on
/// the UI thread, one reload at a time: a batch arriving while it is still reading is kept for it and handed over the
/// moment it finishes, so two reads never interleave on one screen.</para>
/// </summary>
public sealed class ChangeFeed<TEvent>(IEventBus eventBus, ILogger logger, TimeSpan window) : IDisposable
    where TEvent : IIntegrationEvent
{
    /// <summary>Short enough to read as immediate after a click, long enough that a burst is one reload rather than
    /// one per write.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly List<Listener> _listeners = [];
    private List<TEvent>? _pending;
    private IDisposable? _subscription;

    /// <summary>Calls <paramref name="reload"/> on the UI thread with each batch of changes, in the order they were
    /// published, until the returned handle is disposed.</summary>
    public IDisposable Subscribe(Func<IReadOnlyList<TEvent>, Task> reload)
    {
        var listener = new Listener(reload, logger);
        lock (_gate)
        {
            // Subscribed on first use rather than at construction: a feed nobody listens to has no reason to hear
            // every write.
            _subscription ??= eventBus.Subscribe<TEvent>(_OnChanged);
            _listeners.Add(listener);
        }

        return new Unsubscriber(this, listener);
    }

    /// <summary>Hands a change that is not on the bus through the same window and one-reload-at-a-time rule, rather
    /// than on a second path whose reload could interleave with this one's.</summary>
    public void Announce(TEvent change) => _OnChanged(change);

    private void _OnChanged(TEvent change)
    {
        bool opensWindow;
        lock (_gate)
        {
            opensWindow = _pending is null;
            (_pending ??= []).Add(change);
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
        List<TEvent>? batch;
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
    private sealed class Listener(Func<IReadOnlyList<TEvent>, Task> reload, ILogger logger)
    {
        private List<TEvent>? _queued;
        private bool _isReloading;

        public bool IsRemoved { get; set; }

        public void Deliver(IReadOnlyList<TEvent> batch)
        {
            if (_isReloading)
            {
                (_queued ??= []).AddRange(batch);
                return;
            }

            _ = _ReloadAsync(batch);
        }

        private async Task _ReloadAsync(IReadOnlyList<TEvent> batch)
        {
            _isReloading = true;
            try
            {
                for (IReadOnlyList<TEvent>? next = batch; next is not null && !IsRemoved; next = _TakeQueued())
                {
                    try
                    {
                        await reload(next);
                    }
                    catch (Exception exception)
                    {
                        // A screen that failed one read must still take the next batch, or it would stay stale for the
                        // rest of the session without anyone being told.
                        logger.LogError(exception, "A screen could not read again after a {Signal}", typeof(TEvent).Name);
                    }
                }
            }
            finally
            {
                _isReloading = false;
            }
        }

        private List<TEvent>? _TakeQueued()
        {
            List<TEvent>? queued = _queued;
            _queued = null;
            return queued;
        }
    }

    private sealed class Unsubscriber(ChangeFeed<TEvent> feed, Listener listener) : IDisposable
    {
        private ChangeFeed<TEvent>? _feed = feed;

        public void Dispose()
        {
            _feed?._Remove(listener);
            _feed = null;
        }
    }
}
