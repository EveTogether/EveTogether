namespace EveUtils.Client.Fleet;

/// <summary>What the fleet-metrics screen does with the width it was given.</summary>
/// <param name="IsStacked">The map card sits between the summary and the member list instead of beside both.</param>
/// <param name="HintOnOwnRow">The density hint has a full-width row above the buttons instead of sharing it with them.</param>
public readonly record struct FleetMetricsWidthState(bool IsStacked, bool HintOnOwnRow);

public static class FleetMetricsWidth
{
    public const double ScreenMargin = 28;

    /// <summary>The card's 440 plus the 12 between it and the column beside it.</summary>
    public const double MapCardColumn = 452;

    // Left of the card, the member list needs ~640: the compact row holds its identity down to ~626 and clips its
    // figures below that (ET-154), and the default 1160-wide window leaves 680.
    public const double StackBelow = ScreenMargin + MapCardColumn + 640;

    // The hint is ~100 characters at 11 px; next to the four buttons it needs more than this and wraps, below this it
    // wraps into a column a few words wide.
    public const double HintBesideFrom = 720;

    // A window dragged to exactly a threshold would otherwise flip layouts on every pixel.
    public const double Hysteresis = 20;

    public static FleetMetricsWidthState Resolve(double contentWidth, bool hasMapCard, FleetMetricsWidthState current)
    {
        bool stacked = hasMapCard && _Below(current.IsStacked, contentWidth, StackBelow);
        double listWidth = contentWidth - ScreenMargin - (hasMapCard && !stacked ? MapCardColumn : 0);
        return new FleetMetricsWidthState(stacked, stacked || _Below(current.HintOnOwnRow, listWidth, HintBesideFrom));
    }

    private static bool _Below(bool wasBelow, double width, double threshold) =>
        width < (wasBelow ? threshold + Hysteresis : threshold);
}
