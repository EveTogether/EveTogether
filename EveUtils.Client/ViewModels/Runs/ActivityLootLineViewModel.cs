using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One loot line, valued by type id from ET's own price lookup — never from
/// <see cref="RunLootEntryDto.ClipboardPrice"/>, which is kept as what that inventory window happened to show and
/// is never held for a valuation (Raymond, 2026-09-02). The same rule
/// <c>RunLootViewModel._LoadPricesAsync</c> follows for the running run.
/// </summary>
public sealed partial class ActivityLootLineViewModel : ObservableObject
{
    public ActivityLootLineViewModel(int itemTypeId, string name, long? quantity, decimal? unitPrice, LootKind lootKind)
    {
        ItemTypeId = itemTypeId;
        Name = name;
        QuantityText = quantity is { } counted ? counted.ToString("N0") : "—";
        HasPrice = unitPrice is not null;
        // A market price is per unit, so the quantity is what turns it into a line value; no quantity column means
        // one of it, the same reading SdeInventoryResolver takes.
        Value = unitPrice * (quantity ?? 1);
        ValueText = Value is { } value ? $"{value:N2} ISK" : "no price";
        IsLost = lootKind is LootKind.Lost;
    }

    public int ItemTypeId { get; }

    public string Name { get; }

    /// <summary>The tile shown until the item's own icon is there, or instead of it when images are off — the same
    /// lettered fallback every other hex and tile in the app uses.</summary>
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();

    public string QuantityText { get; }

    public bool HasPrice { get; }

    public decimal? Value { get; }

    public string ValueText { get; }

    /// <summary>Spent rather than picked up. Its own category and never loot with a minus in front of it, which is
    /// the reading <see cref="LootKind"/> has carried since it was written.</summary>
    public bool IsLost { get; }

    /// <summary>The line that carries the most of its character's loot — marked so the one Blood Bronze Tag worth
    /// more than everything else put together is read first rather than found (ET-215 mockup).</summary>
    [ObservableProperty] private bool _isTopValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private Bitmap? _icon;

    public bool HasIcon => Icon is not null;

    /// <summary>The type's icon from the app's own image cache, best-effort: images off or offline leaves the
    /// lettered tile, the way the fit browser's cargo strip does.</summary>
    public async Task LoadIconAsync(ITypeImageProvider images) =>
        Icon = await images.GetImageAsync(ItemTypeId, TypeImageKind.Icon, 32);
}
