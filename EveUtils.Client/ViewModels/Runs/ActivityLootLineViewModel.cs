using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One loot line, valued at the unit price fixed on the line (ET-463), or by type id from ET's own price lookup while
/// it has none — never from <see cref="RunLootEntryDto.ClipboardPrice"/>, which is kept as what that inventory window
/// happened to show and is never held for a valuation (Raymond, 2026-09-02). The same rule
/// <c>RunLootViewModel._LoadPricesAsync</c> follows for the running run.
/// </summary>
public sealed partial class ActivityLootLineViewModel : ObservableObject
{
    public ActivityLootLineViewModel(int itemTypeId, string name, long? quantity, decimal? unitPrice, LootKind lootKind,
        bool isExcluded = false, int captureCount = 1, bool isLivePrice = false, bool isBlueprintAppraisal = false)
    {
        ItemTypeId = itemTypeId;
        Name = name;
        Quantity = quantity;
        UnitPrice = unitPrice;
        HasPrice = unitPrice is not null;
        // A market price is per unit, so the quantity is what turns it into a line value; no quantity column means
        // one of it, the same reading SdeInventoryResolver takes.
        Value = unitPrice * (quantity ?? 1);
        IsLost = lootKind is LootKind.Lost;
        IsExcluded = isExcluded;
        CaptureCount = captureCount;
        IsLivePrice = isLivePrice && unitPrice is not null;
        IsBlueprintAppraisal = isBlueprintAppraisal && unitPrice is not null;
    }

    public int ItemTypeId { get; }

    public string Name { get; }

    public long? Quantity { get; }

    /// <summary>"3×" — the loot table's own count column (ET-215 mockup).</summary>
    public string QuantityText => $"{(Quantity ?? 1).ToString("N0", CultureInfo.CurrentCulture)}×";

    /// <summary>"Metal Scraps ×1" — how a capture lists what it brought in.</summary>
    public string NameWithQuantityText => $"{Name} ×{(Quantity ?? 1).ToString("N0", CultureInfo.CurrentCulture)}";

    public decimal? UnitPrice { get; }

    /// <summary>"@ 144.84" — what one of it cost, for CONSUMED (ET-488), where ammunition runs to cents a round.</summary>
    public string UnitPriceText => UnitPrice is { } price ? $"@ {price.ToString("N2", CultureInfo.InvariantCulture)}" : "no price";

    public bool HasPrice { get; }

    public decimal? Value { get; }

    /// <summary>Valued at today's cache price because the line has no fixed price yet (ET-463) — said in a badge beside
    /// the name, since it still moves with the market until the price is fixed.</summary>
    public bool IsLivePrice { get; }

    /// <summary>Valued as what building the blueprint once earns at EVE average prices (ET-501), not at a market price —
    /// said in a badge beside the name, since a blueprint copy has no market price of its own.</summary>
    public bool IsBlueprintAppraisal { get; }

    public const string LivePriceTip = "Valued at today's EVE average price: this line has no fixed price yet.";

    public const string BlueprintAppraisalTip =
        "Build value on EVE average prices: the product minus its materials and a 9% job cost, for 1 run at ME 0. Never below zero.";

    // The figures carry no marker of their own (ET-503): one appended to the amount pushed that row's quantity and ISK
    // out of the columns every other row lines up in.
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

    [ObservableProperty] private Bitmap? _icon;

    /// <summary>The type's icon from the app's own image cache; a type with no icon (a blueprint, a new item) or no
    /// image at all (images off, offline) gets the bundled placeholder.</summary>
    public async Task LoadIconAsync(ITypeImageProvider images)
    {
        Icon = await images.GetImageAsync(ItemTypeId, TypeImageKind.Icon, 32) ?? TypeImagePlaceholder.Bitmap;
    }
}
