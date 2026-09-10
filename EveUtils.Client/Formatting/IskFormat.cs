using System.Globalization;

namespace EveUtils.Client.Formatting;

/// <summary>ISK formatting for value readouts, shared by the fit-detail value, the type-info card, the fit-browser
/// price column and the appraisal tool so the format lives in one place. Invariant culture keeps the
/// decimal separator a dot, matching the app's English UI and the in-game ISK convention.</summary>
public static class IskFormat
{
    /// <summary>"— ISK" for nothing, otherwise the value compacted to billions/millions or a grouped exact figure.</summary>
    public static string Short(double value) =>
        value <= 0 ? "— ISK"
        : value >= 1e9 ? (value / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + " B ISK"
        : value >= 1e6 ? (value / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + " M ISK"
        : value.ToString("N0", CultureInfo.InvariantCulture) + " ISK";

    /// <summary>The whole figure, grouped, for a readout where the amount itself is the answer and
    /// <see cref="Short"/>'s rounding would hide what it actually is — "1.4 B ISK" covers a span of 10 million.
    /// Reads "— ISK" for nothing, like <see cref="Short"/>, and the two agree below a million.</summary>
    public static string Exact(double value) =>
        value <= 0 ? "— ISK" : value.ToString("N0", CultureInfo.InvariantCulture) + " ISK";

    /// <summary>Like <see cref="Exact"/>, but a real, known zero reads "0 ISK" rather than the dash — for a readout
    /// where "this earned nothing" has to say so plainly rather than read the same as "nothing is known here at
    /// all" (ET-217 review: a run with no loot and no bounty showed a bare "ISK" with nothing beside it, easy to
    /// mistake for a rendering glitch). The caller is the one who knows whether zero is a real answer or an unknown
    /// one — this only ever renders the number it is given.</summary>
    public static string ExactOrZero(double value) =>
        value <= 0 ? "0 ISK" : value.ToString("N0", CultureInfo.InvariantCulture) + " ISK";
}
