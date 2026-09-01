using System.Collections.Generic;

namespace EveUtils.Client.Clipboard;

/// <summary>
/// Reads scan-list rows; the id (column 0), site type (column 2) and name (column 3) are the only values the toast needs.
/// </summary>
public static class ClipboardSignatureParser
{
    public static IReadOnlyList<ClipboardSignatureRow> Parse(string text)
    {
        var rows = new List<ClipboardSignatureRow>();

        foreach (var line in text.Split('\n'))
        {
            var row = line.TrimEnd('\r');
            if (row.Trim().Length == 0)
                continue;

            var fields = row.Split('\t');
            if (fields.Length != 6)
                continue;

            rows.Add(new ClipboardSignatureRow(fields[0].Trim(), NullIfBlank(fields[2]), NullIfBlank(fields[3])));
        }

        return rows;
    }

    private static string? NullIfBlank(string field) => string.IsNullOrWhiteSpace(field) ? null : field.Trim();
}
