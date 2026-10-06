using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Client.Clipboard;

/// <summary>What one block of inventory text turned into: the rows that resolved, how many names did not, and — when
/// nothing resolved — the reason to put in front of the pilot.</summary>
public sealed record InventoryTextReading(
    IReadOnlyList<(AppraisalLine Line, ClipboardInventoryItem Item)> Lines,
    IReadOnlyList<ClipboardInventoryItem> Unresolved,
    string? Refusal,
    bool IsSingleUnknownRow)
{
    public int UnresolvedCount => Unresolved.Count;

    /// <summary>The rows no SDE type carries, by name and merged — what the unrecognised log keeps (ET-460). A row
    /// without a name has nothing to keep.</summary>
    public IReadOnlyList<UnrecognisedLootNameInput> UnrecognisedNames => NamesOf(Unresolved);

    /// <summary>The rows of a copy in which no name is known yet, but which carry an EVE inventory's own volume or
    /// price column (ET-460): a cargo full of new event items. Without those columns "Budget rent 1200" is
    /// indistinguishable from loot, so a copy like that stays refused. Empty whenever any row resolved.</summary>
    public IReadOnlyList<ClipboardInventoryItem> OnlyUnrecognised { get; init; } = [];

    public static IReadOnlyList<UnrecognisedLootNameInput> NamesOf(IEnumerable<ClipboardInventoryItem> items) =>
    [
        .. items
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new UnrecognisedLootNameInput { Name = group.Key, Quantity = group.Sum(item => item.Quantity ?? 1) })
    ];

    /// <summary>The one reading path, shared by the clipboard watch and the window's paste boxes, so the same text
    /// cannot be loot in one place and refused in the other. Whether a refusal is shown, logged or left in the box
    /// is the caller's business; working out that there is one is not.</summary>
    public static InventoryTextReading Read(string text, ISdeAccessor sde)
    {
        bool hasSingleRow = ClipboardInventoryParser.HasSingleRow(text);
        IReadOnlyList<ClipboardInventoryItem> items = ClipboardInventoryParser.Parse(text);
        var resolution = SdeInventoryResolver.ResolveItems(items, sde);
        IReadOnlyList<ClipboardInventoryItem> shapedUnresolved = resolution.Unresolved;
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
            return new InventoryTextReading(resolution.Lines, resolution.Unresolved, Refusal: null, IsSingleUnknownRow: false);

        // It never asks for column headings: an EVE inventory copy carries none.
        string refusal = resolution.Unresolved.Count > 0
            ? $"None of the {resolution.Unresolved.Count} copied names is a known item type. Copy rows from an EVE inventory window."
            : "No column in this copy stands out as the item names. Copy the rows from an EVE inventory window.";
        return new InventoryTextReading([], resolution.Unresolved, refusal, hasNoSdeMatch)
        {
            OnlyUnrecognised = !sde.IsAvailable || resolution.Lines.Count > 0
                ? []
                : [.. shapedUnresolved.Where(item => item.Volume is not null || item.Price is not null)]
        };
    }
}
