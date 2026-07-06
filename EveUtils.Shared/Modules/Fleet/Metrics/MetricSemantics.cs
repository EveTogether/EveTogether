namespace EveUtils.Shared.Modules.Fleet.Metrics;

/// <summary>
/// How a <see cref="MetricKind"/>'s value should be read and aggregated:
/// <list type="bullet">
/// <item><see cref="Rate"/> — an instantaneous per-second figure (DPS, mining/min); fleet-total = sum of the
/// members' current values.</item>
/// <item><see cref="Cumulative"/> — a running total (bounty ISK, total mined); fleet-total = sum of the
/// members' running totals.</item>
/// <item><see cref="State"/> — a position/category (location, ship); latest-value-wins label, NOT a graph and
/// NOT aggregatable. The numeric <c>Value</c> carries an id (e.g. solar_system_id) the client resolves via the
/// SDE.</item>
/// </list>
/// </summary>
public enum MetricSemantics
{
    Rate = 0,
    Cumulative = 1,
    State = 2,
}
