using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
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
    public ActivityLootLineViewModel(int itemTypeId, string name, long? quantity, decimal? unitPrice, LootKind lootKind,
        bool isExcluded = false, int captureCount = 1)
    {
        ItemTypeId = itemTypeId;
        Name = name;
        Quantity = quantity;
        HasPrice = unitPrice is not null;
        // A market price is per unit, so the quantity is what turns it into a line value; no quantity column means
        // one of it, the same reading SdeInventoryResolver takes.
        Value = unitPrice * (quantity ?? 1);
        IsLost = lootKind is LootKind.Lost;
        IsExcluded = isExcluded;
        CaptureCount = captureCount;
    }

    public int ItemTypeId { get; }

    public string Name { get; }

    public long? Quantity { get; }

    /// <summary>The tile shown until the item's own icon is there, or instead of it when images are off — the same
    /// lettered fallback every other hex and tile in the app uses.</summary>
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();

    /// <summary>"3×" — the loot table's own count column (ET-215 mockup).</summary>
    public string QuantityText => $"{(Quantity ?? 1).ToString("N0", CultureInfo.CurrentCulture)}×";

    /// <summary>"Metal Scraps ×1" — how a capture lists what it brought in.</summary>
    public string NameWithQuantityText => $"{Name} ×{(Quantity ?? 1).ToString("N0", CultureInfo.CurrentCulture)}";

    public bool HasPrice { get; }

    public decimal? Value { get; }

    public string ValueText => IskFormat.WholeOrNoPrice(Value);

    /// <summary>The value without its unit, for the columns under a figure that already says ISK.</summary>
    public string AmountText => IskFormat.NumberOrNoPrice(Value);

    /// <summary>Spent rather than picked up. Its own category and never loot with a minus in front of it, which is
    /// the reading <see cref="LootKind"/> has carried since it was written.</summary>
    public bool IsLost { get; }

    /// <summary>What was copied and left out, kept on its own row under what counts rather than folded into it: two
    /// Metal Scraps counted and a third excluded read as "2×" and a struck-through "1×", never as a quiet "2×" that
    /// hides the third (ET-215).</summary>
    public bool IsExcluded { get; }

    /// <summary>How many copies this row was added up from, said when it is more than one — the one row a pilot sees
    /// for three Metal Scraps still admits it came in three times.</summary>
    public int CaptureCount { get; }

    public string? CaptureCountText => CaptureCount > 1 ? $"({CaptureCount} captures)" : null;

    /// <summary>The line that carries the most of its character's loot — marked so the one Blood Bronze Tag worth
    /// more than everything else put together is read first rather than found (ET-215 mockup).</summary>
    [ObservableProperty] private bool _isTopValue;

    /// <summary>Every other row carries the faint stripe the mockup's table has, so a long list stays followable
    /// across the gap between name and value.</summary>
    [ObservableProperty] private bool _isAlternate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private Bitmap? _icon;

    public bool HasIcon => Icon is not null;

    /// <summary>The type's icon from the app's own image cache, best-effort: images off or offline leaves the
    /// lettered tile, the way the fit browser's cargo strip does.</summary>
    public async Task LoadIconAsync(ITypeImageProvider images) =>
        Icon = await images.GetImageAsync(ItemTypeId, TypeImageKind.Icon, 32);
}
