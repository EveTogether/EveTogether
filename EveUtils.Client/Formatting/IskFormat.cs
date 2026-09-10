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

    /// <summary>The whole figure, grouped, with no unit — for a column under a header that already says ISK.
    /// Sign and zero pass straight through: unlike <see cref="Exact"/> and <see cref="ExactOrZero"/>, this is for
    /// a caller that already knows it has a real figure and never mistakes a negative or a zero for "unknown"
    /// (ET-218: the ISK-form reward and loot totals can run negative when what was spent outweighs what came in).</summary>
    public static string Number(decimal value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Like <see cref="Number"/>, with the "ISK" unit.</summary>
    public static string Whole(decimal value) => Number(value) + " ISK";

    /// <summary>"no price" for a figure nobody has, otherwise <see cref="Whole"/> — the null/priced distinction a
    /// valuation readout needs (RunLootViewModel, ActivityLootViewModel and the like), so a real, known zero still
    /// reads "0 ISK" rather than looking unpriced, the same split ET-217 drew for <see cref="ExactOrZero"/>.</summary>
    public static string WholeOrNoPrice(decimal? value) => value is { } v ? Whole(v) : "no price";

    /// <summary>Like <see cref="WholeOrNoPrice"/>, without the "ISK" suffix.</summary>
    public static string NumberOrNoPrice(decimal? value) => value is { } v ? Number(v) : "no price";

    /// <summary>Signed short form with a k/M/B tier ("84.2M", "-1.2M", "1.2k", "965") — unit-free so the caller
    /// supplies the noun. Below 1,000 ISK this is the whole rounded number: the compacted tiers above it keep a
    /// couple of decimals of their own unit (thousands, millions, billions), which is a reduced-precision view of
    /// a big figure rather than the ISK-cents this format never shows raw.</summary>
    public static string Compact(decimal value)
    {
        string sign = value < 0 ? "-" : string.Empty;
        decimal size = Math.Abs(value);
        return size switch
        {
            >= 1_000_000_000m => sign + (size / 1_000_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + "B",
            >= 1_000_000m => sign + (size / 1_000_000m).ToString("0.##", CultureInfo.InvariantCulture) + "M",
            >= 1_000m => sign + (size / 1_000m).ToString("0.#", CultureInfo.InvariantCulture) + "k",
            _ => sign + size.ToString("N0", CultureInfo.InvariantCulture)
        };
    }
}
