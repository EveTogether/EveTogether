using System;
using System.Text.RegularExpressions;

namespace EveUtils.Client.Clipboard;

/// <summary>
/// Decides on shape alone whether a clipboard payload is an EVE fit or an inventory listing. It deliberately does
/// not parse: a subscriber that wants the contents runs
/// <see cref="EveUtils.Shared.Modules.Fittings.Services.Parsers.IFitTextImporter"/> itself. Recognition has to be
/// cheap, because it runs on every copy of the day, and strict, because everything it does not recognise is
/// dropped unread.
/// </summary>
public static partial class ClipboardShapeRecogniser
{
    private const int MinimumInventoryRows = 2;

    /// <summary>
    /// EVE's own fit export always writes <c>[Ship, Fit name]</c>. <c>IFitTextImporter.Detect</c> accepts any text
    /// starting with <c>[</c> — right for a paste window, where the user has already said "this is a fit", and far
    /// too loose for a hook that sees every copy. The strict header is what makes the recognition cheap and safe.
    /// </summary>
    [GeneratedRegex(@"^\[[^\[\]\r\n]+,[^\[\]\r\n]*\]$")]
    private static partial Regex FitHeader();

    public static ClipboardShape Recognise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ClipboardShape.Unrecognised;

        if (FirstNonEmptyLine(text) is { } header && FitHeader().IsMatch(header))
            return ClipboardShape.Fit;

        return IsInventoryTable(text) ? ClipboardShape.Inventory : ClipboardShape.Unrecognised;
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
    // tab-separated fields.
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
