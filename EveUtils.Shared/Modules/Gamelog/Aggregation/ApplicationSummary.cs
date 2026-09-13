using System.Globalization;
using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// One pilot's application as a screen shows it and as the fleet wire carries it (ET-277): the main weapon's verdict
/// and percentage, plus a one-line per-weapon breakdown. Small on purpose — it rides one fleet metric sample, the
/// verdict and percentage folded into its number (<see cref="ToWireValue"/>) and the breakdown in its text.
/// </summary>
public sealed record ApplicationSummary(ApplicationVerdict Verdict, double? Percent, string? Breakdown)
{
    /// <summary>Below this the shots land badly enough to act on.</summary>
    public const double AdjustBelow = 55;

    /// <summary>From here the shots land solidly.</summary>
    public const double SweetSpotFrom = 80;

    public static readonly ApplicationSummary Idle = new(ApplicationVerdict.Idle, null, null);

    public static ApplicationVerdict VerdictFor(double percent) =>
        percent >= SweetSpotFrom ? ApplicationVerdict.SweetSpot
        : percent < AdjustBelow ? ApplicationVerdict.Adjust
        : ApplicationVerdict.Ok;

    /// <summary>A measured percentage travels as itself (0–100); a verdict without one as a negative code, so the one
    /// number can never be read as a percentage it is not.</summary>
    public double ToWireValue() => Percent is { } percent && IsMeasured(Verdict) ? percent : -1 - (int)Verdict;

    /// <summary>The receiving side of <see cref="ToWireValue"/>. A code a newer client may send reads as idle.</summary>
    public static ApplicationSummary FromWire(double value, string? breakdown)
    {
        if (value >= 0)
            return new ApplicationSummary(VerdictFor(value), Math.Min(value, 100), breakdown);

        var verdict = (ApplicationVerdict)(int)(-1 - value);
        return Enum.IsDefined(verdict) && !IsMeasured(verdict)
            ? new ApplicationSummary(verdict, null, breakdown)
            : Idle;
    }

    public static bool IsMeasured(ApplicationVerdict verdict) =>
        verdict is ApplicationVerdict.Adjust or ApplicationVerdict.Ok or ApplicationVerdict.SweetSpot;

    /// <summary>How one weapon reads in a breakdown line: its percentage, "n/a" when the log cannot say, "learning" while
    /// a missile has no full volley to go by yet, "—" while there are too few shots.</summary>
    public static string Label(ApplicationVerdict verdict, double? percent) => verdict switch
    {
        ApplicationVerdict.NotMeasurable => "n/a",
        ApplicationVerdict.Learning => "learning",
        _ when percent is { } value && IsMeasured(verdict) => value.ToString("0", CultureInfo.InvariantCulture) + "%",
        _ => "—",
    };
}
