using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Client.UiTests;

/// <summary>Test metric source that emits exactly one sample of a fixed kind on every tick.</summary>
public sealed class FixedMetricSource(MetricKind kind, double value = 1) : IFleetMetricSource
{
    public IEnumerable<MetricSample> Sample(long fleetId, int characterId, long unixMs)
    {
        yield return new MetricSample(characterId, fleetId, kind, value, unixMs);
    }
}
