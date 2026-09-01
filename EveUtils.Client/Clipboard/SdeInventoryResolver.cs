using System.Collections.Generic;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.Clipboard;

public static class SdeInventoryResolver
{
    public static (IReadOnlyList<AppraisalLine> Lines, IReadOnlyList<string> Unresolved) Resolve(
        IReadOnlyList<ClipboardInventoryItem> items, ISdeAccessor sde)
    {
        List<AppraisalLine> lines = [];
        List<string> unresolved = [];
        foreach (ClipboardInventoryItem item in items)
        {
            if (sde.TryGetTypeId(item.Name, out int typeId))
                lines.Add(new AppraisalLine(typeId, item.Name, item.Quantity ?? 1)); // no quantity column = one of it
            else
                unresolved.Add(item.Name);
        }

        return (lines, unresolved);
    }
}
