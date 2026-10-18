namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>
/// Weighted quantiles with the midpoint rule (Hyndman-Fan type 5, weighted): each value stands at the middle of its
/// own weight along the cumulative weight, and a quantile between two values is interpolated linearly. With equal
/// weights the median of 1, 2, 3, 4 is 2.5, and a single value is every quantile.
/// </summary>
internal static class WeightedQuantile
{
    /// <param name="sorted">Ascending by value, every weight above 0, at least one entry.</param>
    /// <param name="probability">0-1.</param>
    public static decimal Of(IReadOnlyList<(decimal Value, double Weight)> sorted, double probability)
    {
        double total = sorted.Sum(entry => entry.Weight);
        double target = probability * total;
        double passed = 0;
        double previousCentre = 0;
        decimal previousValue = sorted[0].Value;
        for (int index = 0; index < sorted.Count; index++)
        {
            (decimal value, double weight) = sorted[index];
            double centre = passed + weight / 2;
            if (target <= centre)
            {
                if (index == 0)
                    return value;

                double fraction = (target - previousCentre) / (centre - previousCentre);
                return previousValue + (value - previousValue) * (decimal)fraction;
            }

            passed += weight;
            previousCentre = centre;
            previousValue = value;
        }

        return sorted[^1].Value;
    }
}
