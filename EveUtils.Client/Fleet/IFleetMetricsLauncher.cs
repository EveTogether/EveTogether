namespace EveUtils.Client.Fleet;

/// <summary>
/// Opens the live metrics window for a server fleet from anywhere (the fleet list, an inbox toast) without the
/// caller needing the fleet's already-loaded <see cref="FleetInfo"/>. A single seam so the "open metrics"
/// behaviour lives in one place.
/// </summary>
public interface IFleetMetricsLauncher
{
    /// <summary>Open the metrics window for <paramref name="fleetId"/> on <paramref name="serverAddress"/>, acting
    /// as <paramref name="actingCharacterId"/>. Pass <paramref name="fleet"/> when the caller already has it to skip
    /// the fetch; otherwise it is fetched. No-op when the fleet cannot be resolved.</summary>
    Task LaunchAsync(string serverAddress, long fleetId, int actingCharacterId, FleetInfo? fleet = null,
        CancellationToken cancellationToken = default);
}
