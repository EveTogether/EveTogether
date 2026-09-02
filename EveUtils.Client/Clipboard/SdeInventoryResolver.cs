using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.Clipboard;

public static class SdeInventoryResolver
{
    /// <summary>Each resolved line keeps the row it came from, so a caller that needs the volume and price columns
    /// does not have to resolve the names a second time.</summary>
    public static (IReadOnlyList<(AppraisalLine Line, ClipboardInventoryItem Item)> Lines, IReadOnlyList<string> Unresolved) Resolve(
        IReadOnlyList<ClipboardInventoryItem> items, ISdeAccessor sde)
    {
        List<(AppraisalLine, ClipboardInventoryItem)> lines = [];
        List<string> unresolved = [];
        foreach (ClipboardInventoryItem item in items)
        {
            if (sde.TryGetTypeId(item.Name, out int typeId))
                lines.Add((new AppraisalLine(typeId, item.Name, item.Quantity ?? 1), item)); // no quantity column = one of it
            else
                unresolved.Add(item.Name);
        }

        return (lines, unresolved);
    }

    /// <summary>
    /// Which candidate column actually holds item names: the one whose values the SDE recognises most of. This is
    /// what tells "Metal Scraps" from "Commodities" on a two-row copy, where counting distinct values cannot.
    /// A tie is still no answer, and so is nothing matching at all.
    /// </summary>
    internal static (IReadOnlyList<(AppraisalLine Line, ClipboardInventoryItem Item)> Lines, IReadOnlyList<string> Unresolved)
        ResolveBestCandidate(IReadOnlyList<IReadOnlyList<ClipboardInventoryItem>> columns, ISdeAccessor sde,
            out bool hasNoSdeMatch)
    {
        (IReadOnlyList<(AppraisalLine Line, ClipboardInventoryItem Item)> Lines, IReadOnlyList<string> Unresolved) best = ([], []);
        var bestCount = 0;
        var tied = false;

        foreach (IReadOnlyList<ClipboardInventoryItem> column in columns)
        {
            var resolution = Resolve(column, sde);
            if (resolution.Lines.Count > bestCount)
            {
                best = resolution;
                bestCount = resolution.Lines.Count;
                tied = false;
            }
            else if (resolution.Lines.Count == bestCount && bestCount > 0)
            {
                tied = true;
            }
        }

        hasNoSdeMatch = bestCount == 0;
        // Nothing matched and two columns matching equally are different refusals, so they carry different
        // evidence: the names that were looked up, or none at all.
        if (bestCount == 0)
            return ([], [.. columns.SelectMany(column => column.Select(item => item.Name))]);

        return tied ? ([], []) : best;
    }
}
