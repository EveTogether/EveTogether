using Avalonia.Threading;
using EveUtils.Client.Platform;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.EveSettings;

/// <summary>
/// The fan-out behind <see cref="IEveSettingsWatch"/>, and the place the two sources of "something moved" are folded
/// into one stream:
///
/// <list type="number">
/// <item>the 5-second client sweep, whose evidence changes when a pilot logs in or out;</item>
/// <item>the automatic sync's own pass, which is what notices a client process appearing or disappearing — including
/// one on the login screen that no game log ever mentions, and which is exactly the case that left a stale banner on
/// screen (ET-68).</item>
/// </list>
///
/// A screen subscribes once and hears both, rather than subscribing twice and reconciling two refresh paths.
/// </summary>
public sealed class EveSettingsWatch : IEveSettingsWatch, ISingletonService, IDisposable
{
    private readonly EveClientPresenceService? _presence;
    private readonly Action<EveClientEvidence>? _onEvidence;
    private readonly List<Action<EveSettingsChange>> _handlers = [];
    private readonly object _gate = new();

    public EveSettingsWatch(EveClientPresenceService? presence = null)
    {
        _presence = presence;
        if (presence is null)
            return;

        // Someone logging in or out shows up here within five seconds; a client starting or closing with nobody
        // logged in does not, and comes in from the automatic sync's pass instead.
        _onEvidence = _ => Announce(new EveSettingsChange(EveSettingsChangeKind.Clients, ProbeClients()));
        presence.Changed += _onEvidence;
    }

    public EveClientPresenceSnapshot ProbeClients()
    {
        if (_presence is null)
            return EveClientPresenceSnapshot.None;

        return new EveClientPresenceSnapshot(
            _presence.RunningClientCount(),
            _presence.Current.CharacterNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public void Announce(EveSettingsChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        Action<EveSettingsChange>[] handlers;
        lock (_gate)
            handlers = [.. _handlers];
        if (handlers.Length == 0)
            return;

        // Announced from the UI thread — a button press — the screens update in the same turn. From a background
        // pass it is marshalled, since every handler touches bound state. Handlers are snapshotted first, so one
        // subscribing or unsubscribing while this runs is safe.
        if (Dispatcher.UIThread.CheckAccess())
            _Deliver(handlers, change);
        else
            Dispatcher.UIThread.Post(() => _Deliver(handlers, change));
    }

    public IDisposable Subscribe(Action<EveSettingsChange> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
            _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    public void Dispose()
    {
        if (_presence is not null && _onEvidence is not null)
            _presence.Changed -= _onEvidence;
    }

    private static void _Deliver(Action<EveSettingsChange>[] handlers, EveSettingsChange change)
    {
        foreach (var handler in handlers)
        {
            // One screen throwing may not cost the others their update — a change reaching every open screen is the
            // whole point, so it cannot stop at the first one that stumbles.
            try { handler(change); }
            catch (Exception) { /* a screen that cannot refresh is its own problem */ }
        }
    }

    private void _Unsubscribe(Action<EveSettingsChange> handler)
    {
        lock (_gate)
            _handlers.Remove(handler);
    }

    private sealed class Subscription(EveSettingsWatch watch, Action<EveSettingsChange> handler) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            watch._Unsubscribe(handler);
        }
    }
}
