namespace EveUtils.Shared.Modules.Fleet.Metrics;

/// <summary>
/// The per-kind metric registry: maps each <see cref="MetricKind"/> to its <see cref="MetricDescriptor"/>
/// and computes a fleet-total for aggregatable kinds. Open-for-extension: an unknown kind degrades to a
/// non-aggregatable State descriptor rather than throwing, so a newer client's kind can still travel the bus.
/// </summary>
public static class FleetMetricCatalog
{
    private static readonly IReadOnlyDictionary<MetricKind, MetricDescriptor> Descriptors = new Dictionary<MetricKind, MetricDescriptor>
    {
        [MetricKind.Dps] = new(MetricKind.Dps, MetricSemantics.Rate, Aggregatable: true, Unit: "dps"),
        [MetricKind.DpsIn] = new(MetricKind.DpsIn, MetricSemantics.Rate, Aggregatable: true, Unit: "dps"),
        [MetricKind.MiningYield] = new(MetricKind.MiningYield, MetricSemantics.Rate, Aggregatable: true, Unit: "m3/min"),
        [MetricKind.Bounty] = new(MetricKind.Bounty, MetricSemantics.Cumulative, Aggregatable: true, Unit: "ISK"),
        [MetricKind.Location] = new(MetricKind.Location, MetricSemantics.State, Aggregatable: false, Unit: "system"),
        [MetricKind.Neut] = new(MetricKind.Neut, MetricSemantics.Rate, Aggregatable: true, Unit: "GJ/s"),
        [MetricKind.Cap] = new(MetricKind.Cap, MetricSemantics.Rate, Aggregatable: true, Unit: "GJ/s"),
        [MetricKind.MiningLedger] = new(MetricKind.MiningLedger, MetricSemantics.Cumulative, Aggregatable: true, Unit: "m3"),
    };

    /// <summary>The descriptor for a kind; an unknown kind degrades to a non-aggregatable State descriptor.</summary>
    public static MetricDescriptor Describe(MetricKind kind) =>
        Descriptors.TryGetValue(kind, out var descriptor)
            ? descriptor
            : new MetricDescriptor(kind, MetricSemantics.State, Aggregatable: false, Unit: string.Empty);

    public static bool IsAggregatable(MetricKind kind) => Describe(kind).Aggregatable;

    /// <summary>
    /// The fleet-total for an aggregatable kind: the sum of the members' values (a Rate sums current values, a
    /// Cumulative sums running totals). Returns null for a non-aggregatable State kind — those are labels, not
    /// rollups.
    /// </summary>
    public static double? Aggregate(MetricKind kind, IEnumerable<double> values)
    {
        var descriptor = Describe(kind);
        return descriptor.Aggregatable ? values.Sum() : null;
    }
}
