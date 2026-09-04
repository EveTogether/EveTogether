using System;
using System.Text.RegularExpressions;

namespace EveUtils.Client.Clipboard;

/// <summary>
/// Decides on shape alone whether a clipboard payload is an EVE fit, an inventory listing or a copied scan signature
/// list — it deliberately does not parse, that is left to whichever subscriber wants the contents. Recognition has
/// to be cheap, because it runs on every copy of the day, and strict, because everything it does not recognise is
/// dropped unread.
/// </summary>
public static partial class ClipboardShapeRecogniser
{
    private const int MinimumInventoryRows = 1;

    /// <summary>
    /// EVE's own fit export always writes <c>[Ship, Fit name]</c>. <c>IFitTextImporter.Detect</c> accepts any text
    /// starting with <c>[</c> — right for a paste window, where the user has already said "this is a fit", and far
    /// too loose for a hook that sees every copy. The strict header is what makes the recognition cheap and safe.
    /// </summary>
    [GeneratedRegex(@"^\[[^\[\]\r\n]+,[^\[\]\r\n]*\]$")]
    private static partial Regex FitHeader();

    // ET-79 §7: three letters, a dash, three digits is what every reviewed external parser leaves unvalidated —
    // a vermoeden, not a measurement. Loosen this anchor once a live client capture confirms the real pattern.
    [GeneratedRegex(@"^[A-Za-z]{3}-\d{3}$")]
    private static partial Regex SignatureId();

    private const string MissionObjectivesHeaderSuffix = " Objectives";

    public static ClipboardShape Recognise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ClipboardShape.Unrecognised;

        if (FirstNonEmptyLine(text) is { } header && FitHeader().IsMatch(header))
            return ClipboardShape.Fit;

        // A mission block's header lines carry no tabs, so it can never satisfy IsInventoryTable's equal-tab-count
        // rule (ET-175 AC-2) — but it is checked here anyway, ahead of Signature and Inventory, for the same reason
        // Signature is checked ahead of Inventory: the strictest, most specific shape decides first.
        if (IsMissionShape(text))
            return ClipboardShape.Mission;

        // Checked before IsInventoryTable: a copy of several signature rows also has an equal tab count on every
        // row, so the inventory check would otherwise claim it first (ET-79 AC-2).
        if (IsSignatureTable(text))
            return ClipboardShape.Signature;

        return IsInventoryTable(text) ? ClipboardShape.Inventory : ClipboardShape.Unrecognised;
    }

    // ET-175: a mission capture opens with "<agent> Objectives" and carries a "Rewards" block further down. Both
    // are required — either word alone is common enough in ordinary copied text to misfire — but nothing past
    // that is checked here, because recognition only decides the shape, not the content (ClipboardMissionParser).
    private static bool IsMissionShape(string text)
    {
        if (FirstNonEmptyLine(text) is not { } header
            || header.Length <= MissionObjectivesHeaderSuffix.Length
            || !header.EndsWith(MissionObjectivesHeaderSuffix, StringComparison.Ordinal))
            return false;

        foreach (var line in text.Split('\n'))
        {
            if (line.TrimEnd('\r').Trim() == "Rewards")
                return true;
        }

        return false;
    }

    // A scan-window copy is six tab-separated fields per row, the same in every language the client can run: no
    // word from the EVE UI is used as an anchor, only the id pattern and the scan-percentage column (ET-79 §4).
    private static bool IsSignatureTable(string text)
    {
        var rows = 0;

        foreach (var line in text.Split('\n'))
        {
            var row = line.TrimEnd('\r');
            if (row.Trim().Length == 0)
                continue;

            var fields = row.Split('\t');
            if (fields.Length != 6 || !SignatureId().IsMatch(fields[0]) || !fields[4].TrimEnd().EndsWith('%'))
                return false;

            rows++;
        }

        return rows > 0;
    }

    private static string? FirstNonEmptyLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return null;
    }

    // An inventory copy carries whichever columns the window happened to show, so there is no header row to key
    // on: the only stable signal is the table shape — more than one row, every row cut into the same number of
    // tab-separated fields; one row relies on the SDE candidate gate before storage.
    private static bool IsInventoryTable(string text)
    {
        var expectedTabs = -1;
        var rows = 0;

        foreach (var line in text.Split('\n'))
        {
            var row = line.TrimEnd('\r');
            if (row.Trim().Length == 0)
                continue;

            var tabs = CountTabs(row);
            if (tabs == 0)
                return false;
            if (expectedTabs < 0)
                expectedTabs = tabs;
            else if (tabs != expectedTabs)
                return false;

            rows++;
        }

        return rows >= MinimumInventoryRows;
    }

    private static int CountTabs(ReadOnlySpan<char> row)
    {
        var tabs = 0;
        foreach (var character in row)
        {
            if (character == '\t')
                tabs++;
        }

        return tabs;
    }
}
