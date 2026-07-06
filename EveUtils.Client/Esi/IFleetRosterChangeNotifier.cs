using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Fleet;

namespace EveUtils.Client.Esi;

/// <summary>
/// Turns the boss-side roster diff into transient toasts for the changes a user would otherwise miss with
/// the roster window closed: a planned pilot joining or leaving the live fleet, or an unplanned pilot showing up.
/// Driven by <see cref="EsiFleetSyncService"/> on each 5s poll with the previous and current diff.
/// </summary>
public interface IFleetRosterChangeNotifier
{
    /// <summary>Toasts the transitions between <paramref name="previous"/> and <paramref name="current"/>. A
    /// <c>null</c> <paramref name="previous"/> (the first time we see a fleet) is a silent baseline — no toast.</summary>
    Task NotifyAsync(FleetRosterDiff? previous, FleetRosterDiff current, CancellationToken cancellationToken = default);
}
