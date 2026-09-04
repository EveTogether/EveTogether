using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One loot line on the activity detail, valued by type id from ET's own price lookup — never from
/// <see cref="RunLootEntryDto.ClipboardPrice"/>, which is kept as what that inventory window happened to show and
/// is never held for a valuation (Raymond, 2026-09-02). The same rule
/// <c>RunLootViewModel._LoadPricesAsync</c> follows for the running run.
/// </summary>
public sealed class ActivityLootLineViewModel
{
    public ActivityLootLineViewModel(RunLootEntryDto entry, decimal? unitPrice)
    {
        Name = entry.Name;
        QuantityText = entry.Quantity is { } quantity ? quantity.ToString("N0") : "—";
        HasPrice = unitPrice is not null;
        // A market price is per unit, so the quantity is what turns it into a line value; no quantity column means
        // one of it, the same reading SdeInventoryResolver takes.
        ValueText = unitPrice is { } price ? $"{price * (entry.Quantity ?? 1):N2} ISK" : "no price";
    }

    public string Name { get; }

    public string QuantityText { get; }

    public bool HasPrice { get; }

    public string ValueText { get; }
}
