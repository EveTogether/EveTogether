using System.Collections.Generic;

namespace EveUtils.Client.Clipboard;

/// <summary>
/// Reads the id, site type and name required by the toast; kind, scan percentage and distance are left unparsed because the toast does not show them.
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
