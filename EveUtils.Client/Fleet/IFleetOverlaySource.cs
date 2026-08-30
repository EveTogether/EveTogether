using System.Collections.Generic;
using EveUtils.Client.ViewModels;

namespace EveUtils.Client.Fleet;

/// <summary>
/// What the fleet overlay reads. Implemented by <see cref="ViewModels.FleetMetricsViewModel"/> and by nothing else in
/// production: the overlay shows the very rows and the very badge that screen has already worked out, rather than
/// subscribing to the bus a second time and arriving at its own answer. One judgement, two windows — the same reason
/// the DPS pop-out is handed the tracker instance instead of a copy of it.
///
/// It exists as an interface for the sake of the tests that have to look at this window: a fake fleet is a handful of
/// <see cref="DpsViewModel"/>s, with no service provider, no transport and no bus behind it.
/// </summary>
public interface IFleetOverlaySource
{
    /// <summary>The fleet's name — which fleet this overlay is about, when more than one is open.</summary>
    string FleetName { get; }

    /// <summary>The fleet's id, which is what the overlay's remembered position and size are keyed on.</summary>
    long FleetId { get; }

    /// <summary>The live member rows, exactly as the fleet-metrics screen holds them.</summary>
    IReadOnlyList<DpsViewModel> Members { get; }

    /// <summary>The WITH FC ratio as that screen's header badge shows it (ET-31/ET-63).</summary>
    FleetCommanderPresence CommanderPresence { get; }
}
