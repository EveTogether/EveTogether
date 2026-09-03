using System;
using System.Collections.Generic;
using Avalonia.Threading;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Events;

namespace EveUtils.Client.Fleet;

/// <summary>
/// The in-client fan-out behind <see cref="IFleetRosterWatch"/>, plus the two things that have to happen on a
/// roster change no matter which screen is open:
///
/// <list type="number">
/// <item>the server's <c>fleet.changed</c> is folded into the same stream, so a screen subscribes once and gets a
/// roster change whether it came from this client or another one;</item>
/// <item>a removed pilot is dropped from <see cref="IFleetParticipation"/> here. <see cref="FleetParticipationRefresher"/>
/// rewrites that set wholesale on its own sweeps, but a sweep is a question asked later while a removal is news that
/// has already happened (ET-49) — so "stop publishing for a pilot who is out of the fleet" is a property of the
/// removal, not of the screen that happened to perform it, and belongs with the announcement rather than in one
/// caller.</item>
/// </list>
///
/// Handlers are invoked on the UI thread because every subscriber is a view-model that touches bound collections;
/// the participation drop is not, so it takes effect the instant the removal returns rather than a dispatcher turn
/// later, while a sample for the kicked pilot may still be in flight.
/// </summary>
public sealed class FleetRosterWatch : IFleetRosterWatch, ISingletonService, IDisposable
{
    private readonly IFleetParticipation _participation;
    private readonly IDisposable _fleetChangedSubscription;
    private readonly List<Action<FleetRosterChange>> _handlers = [];
    private readonly object _gate = new();

    public FleetRosterWatch(IEventBus bus, IFleetParticipation participation)
    {
        _participation = participation;

        // A roster change on a SERVER fleet reaches this client as fleet.changed — someone else's join, another of my
        // clients kicking a pilot, the fleet starting. Same news, other origin: republish it here so no screen has to
        // subscribe twice and reconcile two refresh paths of its own.
        _fleetChangedSubscription = bus.Subscribe<FleetChangedEvent>(
            e => Announce(FleetRosterChange.Reloaded(e.FleetId)));
    }

    public void Announce(FleetRosterChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (change.Kind is FleetRosterChangeKind.MemberRemoved && change.CharacterId > 0)
            _participation.Remove(change.FleetId, change.CharacterId);

        Action<FleetRosterChange>[] handlers;
        lock (_gate)
            handlers = [.. _handlers];
        if (handlers.Length == 0)
            return;

        // Announced from the UI thread — which is where every screen action lives — the screens update in the same
        // turn as the action that caused it, so a removed pilot's row is never on screen for a dispatcher turn after
        // the FC removed them. Off the UI thread (a background sweep) it is marshalled, since the handlers all touch
        // bound collections. Handlers are snapshotted first, so one subscribing or unsubscribing here is safe.
        if (Dispatcher.UIThread.CheckAccess())
            Deliver(handlers, change);
        else
            Dispatcher.UIThread.Post(() => Deliver(handlers, change));
    }

    private static void Deliver(Action<FleetRosterChange>[] handlers, FleetRosterChange change)
    {
        foreach (var handler in handlers)
        {
            // One screen throwing may not cost the others their update — the whole point of this seam is that a
            // roster change reaches every open screen, so it cannot stop at the first one that stumbles.
            try { handler(change); }
            catch (Exception) { /* a screen that cannot refresh is its own problem, not the fleet's */ }
        }
    }

    public IDisposable Subscribe(Action<FleetRosterChange> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
            _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    public void Dispose() => _fleetChangedSubscription.Dispose();

    private void Unsubscribe(Action<FleetRosterChange> handler)
    {
        lock (_gate)
            _handlers.Remove(handler);
    }

    private sealed class Subscription(FleetRosterWatch watch, Action<FleetRosterChange> handler) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            watch.Unsubscribe(handler);
        }
    }
}
