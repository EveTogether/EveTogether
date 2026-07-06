using EveUtils.Shared.Modules.Fleet.Metrics;

namespace EveUtils.Shared.Modules.Fleet.Dtos;

/// <summary>
/// One fleet activity sample: the wire payload of <see cref="Events.FleetMetricEvent"/>. <see cref="Value"/>
/// is the numeric figure for a Rate/Cumulative kind. A State kind (e.g. <see cref="MetricKind.Location"/>) instead
/// carries its label directly in <see cref="Text"/> — the gamelog reports the solar-system <i>name</i>, and the SDE
/// store holds no universe data to resolve an id, so the name travels as-is (serialized as JSON over the wire, so an
/// optional field is backward-compatible). <see cref="FleetId"/> scopes delivery to that fleet's active participants
/// <see cref="UnixMs"/> orders samples on the receiver's live graph.
/// </summary>
public sealed record MetricSample(int CharacterId, long FleetId, MetricKind Kind, double Value, long UnixMs, string? Text = null);
