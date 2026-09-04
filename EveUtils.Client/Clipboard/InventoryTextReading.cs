using System.Collections.Generic;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.Clipboard;

/// <summary>What one block of inventory text turned into: the rows that resolved, how many names did not, and — when
/// nothing resolved — the reason to put in front of the pilot.</summary>
public sealed record InventoryTextReading(
    IReadOnlyList<(AppraisalLine Line, ClipboardInventoryItem Item)> Lines,
    int UnresolvedCount,
    string? Refusal,
    bool IsSingleUnknownRow)
{
    /// <summary>The one reading path, shared by the clipboard watch and the window's paste boxes, so the same text
    /// cannot be loot in one place and refused in the other. Whether a refusal is shown, logged or left in the box
    /// is the caller's business; working out that there is one is not.</summary>
    public static InventoryTextReading Read(string text, ISdeAccessor sde)
    {
        bool hasSingleRow = ClipboardInventoryParser.HasSingleRow(text);
        IReadOnlyList<ClipboardInventoryItem> items = ClipboardInventoryParser.Parse(text);
        var resolution = SdeInventoryResolver.Resolve(items, sde);
        bool hasNoSdeMatch = hasSingleRow && sde.IsAvailable && resolution.Lines.Count == 0;
        if (resolution.Lines.Count == 0 && sde.IsAvailable)
        {
            // The column shape alone did not produce item types — it either could not choose, or chose the group
            // column — so ask the SDE which candidate actually reads as item types. Every row count comes through
            // here: it used to be the single-row case only, which left an icons copy of two items working and the
            // same two items in details refused (ET-65).
            resolution = SdeInventoryResolver.ResolveBestCandidate(
                ClipboardInventoryParser.ParseNameColumnCandidates(text), sde, out bool noCandidateMatch);
            hasNoSdeMatch = hasSingleRow && noCandidateMatch;
        }

        if (resolution.Lines.Count > 0)
            return new InventoryTextReading(resolution.Lines, resolution.Unresolved.Count, Refusal: null, IsSingleUnknownRow: false);

        // It never asks for column headings: an EVE inventory copy carries none.
        string refusal = resolution.Unresolved.Count > 0
            ? $"None of the {resolution.Unresolved.Count} copied names is a known item type. Copy rows from an EVE inventory window."
            : "No column in this copy stands out as the item names. Copy the rows from an EVE inventory window.";
        return new InventoryTextReading([], resolution.Unresolved.Count, refusal, hasNoSdeMatch);
    }
}
