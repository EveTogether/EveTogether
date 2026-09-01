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

    internal static (IReadOnlyList<(AppraisalLine Line, ClipboardInventoryItem Item)> Lines, IReadOnlyList<string> Unresolved)
        ResolveUniqueCandidate(IReadOnlyList<ClipboardInventoryItem> candidates, ISdeAccessor sde, out bool hasNoSdeMatch)
    {
        var resolution = Resolve(candidates, sde);
        hasNoSdeMatch = resolution.Lines.Count == 0;
        return resolution.Lines.Count == 1
            ? resolution
            : ([], candidates.Select(candidate => candidate.Name).ToList());
    }
}
