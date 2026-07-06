using System.Text.RegularExpressions;

namespace EveUtils.Shared.Modules.Fittings.Services.Parsers;

/// <summary>
/// Pure (no-SDE) parser for the EFT text format: a <c>[Ship, Fit name]</c> header followed by one line per item.
/// A module may carry a loaded charge after the first <c>", "</c>; a trailing <c> xN</c> is a quantity (drones,
/// cargo, repeated modules). Blank lines separate sections: each item records its section index and whether it
/// carried an <c>xN</c> suffix, so the assembler can route the trailing drones/cargo sections (where cargo modules
/// live) without fitting them into slots — the slot itself is still decided from the SDE, not from text position.
/// </summary>
internal static partial class EftFitParser
{
    [GeneratedRegex(@"\s+x(\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex QuantitySuffix();

    public static RawFit? Parse(string text)
    {
        string? shipName = null;
        var fitName = "Imported fit";
        var items = new List<RawFitItem>();
        var section = 0;
        var pendingSectionBreak = false;

        foreach (var rawLine in text.Replace("\r", "").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                // A blank line (after the header) ends the current section; consecutive blanks collapse to one break.
                if (shipName is not null)
                    pendingSectionBreak = true;
                continue;
            }

            if (shipName is null)
            {
                // The header is the first non-empty line: [Ship, Fit name]. Anything before it is not EFT.
                if (!line.StartsWith('[') || !line.EndsWith(']'))
                    return null;
                var inner = line[1..^1];
                var comma = inner.IndexOf(',');
                if (comma >= 0)
                {
                    shipName = inner[..comma].Trim();
                    fitName = inner[(comma + 1)..].Trim();
                }
                else
                {
                    shipName = inner.Trim();
                }
                if (string.IsNullOrWhiteSpace(fitName))
                    fitName = "Imported fit";
                continue;
            }

            if (pendingSectionBreak)
            {
                section++;
                pendingSectionBreak = false;
            }

            var quantity = 1;
            var explicitQuantity = false;
            var match = QuantitySuffix().Match(line);
            if (match.Success)
            {
                quantity = int.Parse(match.Groups[1].Value);
                explicitQuantity = true;
                line = line[..match.Index].Trim();
            }

            string name = line;
            string? charge = null;
            var separator = line.IndexOf(", ", StringComparison.Ordinal);
            if (separator >= 0)
            {
                name = line[..separator].Trim();
                charge = line[(separator + 2)..].Trim();
            }

            if (name.Length > 0)
                items.Add(new RawFitItem(name, TypeId: null, quantity, charge, section, explicitQuantity));
        }

        return shipName is null ? null : new RawFit(shipName, ShipTypeId: null, fitName, items);
    }
}
