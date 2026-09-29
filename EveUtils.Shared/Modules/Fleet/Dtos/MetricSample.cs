using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// One fleet activity sample: the wire payload of <see cref="Events.FleetMetricEvent"/>. <see cref="Value"/>
/// is the numeric figure for a Rate/Cumulative kind. A State kind carries its label in <see cref="Text"/> (serialized
/// as JSON over the wire, so an optional field is backward-compatible). <see cref="MetricKind.Location"/> carries both:
/// the solar-system id in <see cref="Value"/> (since ET-394; 0 from older clients or when the SDE did not know the
/// name) and the name in <see cref="Text"/>, which older receivers still read. <see cref="FleetId"/> scopes delivery
/// to that fleet's active participants; <see cref="UnixMs"/> orders samples on the receiver's live graph.
///
/// <see cref="AbyssalAnchorMs"/> rides along with a <see cref="MetricKind.Location"/> sample rather than reusing
/// <see cref="Value"/>: that field already carries a solar-system id when the sample comes from
/// <c>LocationMetricSource</c>, and one field meaning two things is how a stray number gets read as the wrong one.
/// </summary>
public sealed record MetricSample(
    int CharacterId,
    long FleetId,
    MetricKind Kind,
    double Value,
    long UnixMs,
    string? Text = null,
    long AbyssalAnchorMs = 0);
