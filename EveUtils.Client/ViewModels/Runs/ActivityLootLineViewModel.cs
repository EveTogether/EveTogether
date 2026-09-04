using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One loot line, valued by type id from ET's own price lookup — never from
/// <see cref="RunLootEntryDto.ClipboardPrice"/>, which is kept as what that inventory window happened to show and
/// is never held for a valuation (Raymond, 2026-09-02). The same rule
/// <c>RunLootViewModel._LoadPricesAsync</c> follows for the running run.
/// </summary>
public sealed class ActivityLootLineViewModel
{
    public ActivityLootLineViewModel(RunLootEntryDto entry, decimal? unitPrice)
        : this(entry.Name, entry.Quantity, unitPrice, entry.LootKind)
    {
    }

    public ActivityLootLineViewModel(string name, long? quantity, decimal? unitPrice, LootKind lootKind)
    {
        Name = name;
        QuantityText = quantity is { } counted ? counted.ToString("N0") : "—";
        HasPrice = unitPrice is not null;
        // A market price is per unit, so the quantity is what turns it into a line value; no quantity column means
        // one of it, the same reading SdeInventoryResolver takes.
        ValueText = unitPrice is { } price ? $"{price * (quantity ?? 1):N2} ISK" : "no price";
        IsLost = lootKind is LootKind.Lost;
    }

    public string Name { get; }

    public string QuantityText { get; }

    public bool HasPrice { get; }

    public string ValueText { get; }

    /// <summary>Spent rather than picked up. Its own category and never loot with a minus in front of it, which is
    /// the reading <see cref="LootKind"/> has carried since it was written.</summary>
    public bool IsLost { get; }
}
