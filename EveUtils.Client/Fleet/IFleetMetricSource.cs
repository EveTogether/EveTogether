using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Client.Fleet;

/// <summary>
/// A producer of fleet activity samples for the current tick. The <see cref="FleetMetricPublisher"/> polls
/// every registered source ~1 Hz while the client is participating in a fleet, stamping the supplied scope onto
/// each sample. A source is identity-agnostic — it is told the fleet, character and timestamp — so adding a new
/// metric kind (mining yield, bounty, …) is just a new source, no publisher or protocol change.
/// </summary>
public interface IFleetMetricSource
{
    /// <summary>
    /// The samples this source has for the given scope right now. Zero, one or many — a source with nothing to
    /// report returns an empty sequence. Implementations must not block.
    /// </summary>
    IEnumerable<MetricSample> Sample(long fleetId, int characterId, long unixMs);
}
