using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs.Attendance;

/// <summary>
/// The one line under HOMEFRONT's outcome switch, in the run window and on the detail screen alike (ET-271): how the
/// site ended, N, what the table pays each character at N, and what that adds up to on this client —
/// "Completed · 5 in site · 15,000,000 each · 75,000,000 ISK total".
///
/// The total is the Local rows' own figures, a typed one standing in for the table's, so it is always the payout part of
/// TOTAL ISK: a character on someone else's client counts for N and never for this client's total.
/// </summary>
public static class HomefrontPayoutSummary
{
    /// <param name="table">The table's figure at N, whatever the outcome; null for AAR or beyond the table.</param>
    /// <param name="owed">What each character is owed right now — null until the site reads Completed, or AAR has paid
    /// waves.</param>
    public static string Describe(bool isAar, HomefrontOutcome? outcome, int completedWaveCount, int? n,
        decimal? table, decimal? owed, IEnumerable<AttendanceRowViewModel> rows)
    {
        if (n is not { } count)
            return string.Empty;

        string inSite = $"{count} in site";
        AttendanceRowViewModel[] paid = [.. rows.Where(row => row.IsPayoutShown)];
        decimal localTotal = paid.Sum(row => row.CorrectedPayoutIsk ?? row.ExpectedPayoutIsk ?? 0m);
        string total = paid.Length == count
            ? $"{IskFormat.Whole(localTotal)} total"
            : $"{IskFormat.Whole(localTotal)} for {paid.Length} Local";

        if (isAar)
            return owed is { } perCharacter
                ? $"{completedWaveCount} of 9 waves · {inSite} · {IskFormat.Number(perCharacter)} each · {total}"
                : $"{completedWaveCount} of 9 waves · {inSite} · no payout";

        return outcome switch
        {
            HomefrontOutcome.Completed when owed is { } each => $"Completed · {inSite} · {IskFormat.Number(each)} each · {total}",
            HomefrontOutcome.Completed => $"Completed · {inSite} · beyond the payout table",
            HomefrontOutcome.Failed => $"Failed · {inSite} · no payout",
            HomefrontOutcome.Unknown => $"Unknown · {inSite} · no payout counted",
            _ when table is { } ifCompleted => $"Not decided · {inSite} · {IskFormat.Number(ifCompleted)} each if completed",
            _ => $"Not decided · {inSite}"
        };
    }
}
