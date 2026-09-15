using Avalonia.Controls;

namespace EveUtils.Client.Views.Runs.Sections;

/// <summary>When MINING's table reads one line per row and when two (ET-288) — shared by both MINING views, which
/// share the table's styles too (MiningLedgerStyles.axaml).</summary>
internal static class MiningLedger
{
    // The six figure columns take 312 px at the wide spacing (64 + 24 + 42 + 36 + 48 + 62 + 6 × 6); "Conflagrati
    // Mutanite", about the longest ore name there is, needs 132 px beside them with its indent. Both measured in the
    // ET-288 render harness, where the run window's default 560 px leaves 451 px and its 420 px minimum 311 px.
    public const double NarrowBelow = 446;

    public static void FitTo(StackPanel ledger, double width)
    {
        bool isNarrow = width < NarrowBelow;
        if (isNarrow == ledger.Classes.Contains("narrow"))
            return;

        if (isNarrow)
            ledger.Classes.Add("narrow");
        else
            ledger.Classes.Remove("narrow");
    }
}
