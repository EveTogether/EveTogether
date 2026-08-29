using System;

namespace EveUtils.Client.Fleet;

/// <summary>
/// The one place a fleet's roster change is announced, and the one place a screen showing that roster subscribes.
///
/// An action on a fleet member belongs to the fleet, not to the screen it was performed on: removing a pilot in
/// fleet metrics has to reach the fleet browser's card, the roster window and the DPS pop-out just as much as it
/// reaches the screen the FC clicked in. Wiring that as a hand-off between two view-models is what produced ET-46,
/// ET-49 and ET-52 in turn — three rounds of the same "and this neighbour too" — so it is one announcement with
/// however many listeners instead.
///
/// Deliberately NOT the server's <c>fleet.changed</c>: a client-only (local) fleet pushes no such event and never
/// will, and that is exactly the case the operator hits. <c>fleet.changed</c> is folded IN here (see
/// <see cref="FleetRosterWatch"/>) so a screen has one subscription covering both origins, not two.
/// </summary>
public interface IFleetRosterWatch
{
    /// <summary>Tell every open screen that this fleet's roster moved. Called after the change has actually landed
    /// (the transport said yes), so a listener that re-reads the roster reads the new one.</summary>
    void Announce(FleetRosterChange change);

    /// <summary>Listen for roster changes to any fleet — the handler filters on the fleet it shows. Handlers run on
    /// the UI thread. Dispose to stop listening; a screen does that when its window closes.</summary>
    IDisposable Subscribe(Action<FleetRosterChange> handler);
}
