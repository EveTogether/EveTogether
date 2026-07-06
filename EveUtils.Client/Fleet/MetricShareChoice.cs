namespace EveUtils.Client.Fleet;

/// <summary>
/// A per-(fleet, character, metric) sharing choice. <see cref="Inherit"/> follows the global default; the other two
/// override it for this fleet. Maps to the override setting value: Inherit = "" (absent), Share = "true", Off = "false".
/// </summary>
public enum MetricShareChoice
{
    Inherit = 0,
    Share = 1,
    Off = 2,
}
