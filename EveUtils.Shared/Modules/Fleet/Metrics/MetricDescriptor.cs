namespace EveUtils.Shared.Modules.Fleet.Metrics;

/// <summary>
/// Static metadata for one <see cref="MetricKind"/>: its value-semantics, whether it rolls up into a
/// fleet-total, and a display unit. Looked up via <see cref="FleetMetricCatalog"/>.
/// </summary>
public sealed record MetricDescriptor(MetricKind Kind, MetricSemantics Semantics, bool Aggregatable, string Unit);
