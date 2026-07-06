namespace EveUtils.Client.Fleet;

/// <summary>
/// Resolves which fleet metric kinds the user currently shares with the fleet. Reads the persisted client settings
/// and returns an immutable per-tick <see cref="MetricShareSnapshot"/> so a single 1 Hz publish tick makes one
/// settings read and a coherent set of decisions.
/// </summary>
public interface IMetricShareSettings
{
    Task<MetricShareSnapshot> LoadAsync(CancellationToken cancellationToken = default);
}
