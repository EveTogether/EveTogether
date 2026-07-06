using System.Collections.Generic;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>A slot category and its items in the fit-detail list, e.g. "HIGH SLOTS" + the high-slot modules.</summary>
public sealed class FitSlotGroupViewModel
{
    public FitSlotCategory Category { get; }
    public string Header { get; }
    public IReadOnlyList<FitSlotItemViewModel> Items { get; }

    /// <summary>Module/charge count in this group (sums stacked quantities, e.g. 5 drones).</summary>
    public int Count { get; }

    public FitSlotGroupViewModel(FitSlotCategory category, IReadOnlyList<FitSlotItemViewModel> items)
    {
        Category = category;
        Header = FitSlotClassifier.Label(category);
        Items = items;
        var total = 0;
        foreach (var item in items) total += item.Quantity;
        Count = total;
    }
}
