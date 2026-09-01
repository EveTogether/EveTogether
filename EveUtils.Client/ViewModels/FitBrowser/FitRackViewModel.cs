using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>
/// One slot group in a fit card's equipment popover: the rack header, its lines and how many items are in it.
/// Built only for racks that carry something, so the popover never shows an empty "DRONE BAY" heading. The icons
/// load with the popover (<see cref="LoadIconsAsync"/>), not with the card.
/// </summary>
public sealed class FitRackViewModel(FitSlotCategory category, IReadOnlyList<FitModuleLineViewModel> lines)
{
    public FitSlotCategory Category { get; } = category;

    /// <summary>The rack's heading, from the same source the detail window's slot list uses.</summary>
    public string Header { get; } = FitSlotClassifier.Label(category);

    public IReadOnlyList<FitModuleLineViewModel> Lines { get; } = lines;

    /// <summary>Items in this rack, stacked quantities included — five drones on one line still count as five, and
    /// a hold with five thousand rounds in it says so.</summary>
    public int Count { get; } = Total(lines);

    /// <summary>What the popover actually draws. A rack is capped because one is not bounded by anything: EVE's
    /// slots stop at eight, a cargo hold does not, and a hold with twenty kinds of thing in it would push the
    /// popover past the screen it is meant to sit on. The header still carries the true count, and
    /// <see cref="OverflowLabel"/> says what was left out.</summary>
    public IReadOnlyList<FitModuleLineViewModel> VisibleLines { get; } =
        lines.Count <= MaxLines ? lines : lines.Take(MaxLines).ToList();

    public bool HasOverflow => Lines.Count > MaxLines;

    public string OverflowLabel => HasOverflow ? $"+{Lines.Count - MaxLines} more" : "";

    private const int MaxLines = 8;

    public async Task LoadIconsAsync()
    {
        foreach (var line in VisibleLines)
            await line.LoadImageAsync();
    }

    private static int Total(IReadOnlyList<FitModuleLineViewModel> lines)
    {
        var total = 0;
        foreach (var line in lines) total += line.Quantity;
        return total;
    }
}
