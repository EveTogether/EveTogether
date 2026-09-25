namespace EveUtils.Shared.Modules.Skills;

/// <summary>
/// A skill's share of a stat's achievable range: 0 at the fit's base value, 1 at the best any candidate skill
/// reaches for that stat at level V. <paramref name="lowerIsBetter"/> flips the direction for align time and
/// signature radius, where the "best" value is the lowest. Pure — reused by both the impact list's combined
/// per-hour ranking (ET-356) and the plan-optimiser scoring (ET-357).
/// </summary>
public static class StatShare
{
    public static double Compute(double baseValue, double skillValue, double bestValue, bool lowerIsBetter)
    {
        var range = lowerIsBetter ? baseValue - bestValue : bestValue - baseValue;
        if (range <= 0)
            return 0;

        var gain = lowerIsBetter ? baseValue - skillValue : skillValue - baseValue;
        return gain / range;
    }
}
