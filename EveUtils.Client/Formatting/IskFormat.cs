using System.Globalization;

namespace EveUtils.Client.Formatting;

/// <summary>Compact ISK formatting for value readouts (B/M tiers), shared by the fit-detail value, the type-info card
/// and the fit-browser price column so the format lives in one place. Invariant culture keeps the
/// decimal separator a dot, matching the app's English UI and the in-game ISK convention.</summary>
public static class IskFormat
{
    /// <summary>"— ISK" for nothing, otherwise the value compacted to billions/millions or a grouped exact figure.</summary>
    public static string Short(double value) =>
        value <= 0 ? "— ISK"
        : value >= 1e9 ? (value / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + " B ISK"
        : value >= 1e6 ? (value / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + " M ISK"
        : value.ToString("N0", CultureInfo.InvariantCulture) + " ISK";
}
