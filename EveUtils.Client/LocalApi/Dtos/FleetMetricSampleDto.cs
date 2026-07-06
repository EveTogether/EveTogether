using EveUtils.Shared.Modules.Fleet.Dtos;

namespace EveUtils.Client.LocalApi.Dtos;

/// <summary>
/// One live fleet activity sample as pushed over the <c>/ws</c> stream (the <c>fleet.metrics</c> event). <c>Kind</c>
/// is the metric name (Dps/DpsIn/Neut/Cap/Bounty/Location/…); <c>Value</c> is the figure for a rate/cumulative kind,
/// <c>Text</c> the label for a state kind (e.g. the solar-system name). Mirrors the internal sample without leaking it.
/// </summary>
public sealed record FleetMetricSampleDto(
    int CharacterId,
    long FleetId,
    string Kind,
    double Value,
    long UnixMs,
    string? Text)
{
    public static FleetMetricSampleDto FromSample(MetricSample sample) =>
        new(sample.CharacterId, sample.FleetId, sample.Kind.ToString(), sample.Value, sample.UnixMs, sample.Text);
}
