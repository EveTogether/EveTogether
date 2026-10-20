using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EveUtils.Client.Formatting;
using EveUtils.Client.Imaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// One item of a loot table (ET-519). A drop rate is a percentage only where the table and the item have the runs for
/// it; everywhere else it says "seen in 9 of 12", counted in whole runs, so the two are never mixed in one cell. Below
/// the minimum of runs the row is the item's name alone.
///
/// <para>The share bar is drawn without a number while the table is not rated: the order and the bar say as much as the
/// runs can carry. Ink is the plain text colour throughout; the dim one is not used on these rows.</para>
/// </summary>
public sealed partial class LootTableRowViewModel : ViewModelBase
{
    private const double BarScale = 2.2;

    public LootTableRowViewModel(LootTableItemDto item, string name, LootTableConfidence tableConfidence, int tableRuns,
        DateTime nowUtc)
    {
        Item = item;
        NameText = name;
        IsRare = item.IsRare;
        TagText = _TagOf(item.RarityClass);
        ChangeText = item.Change switch
        {
            LootItemChange.NotSeenSinceUpdate => "not seen since update",
            LootItemChange.NewSinceUpdate => "new since update",
            _ => string.Empty
        };
        if (tableConfidence is LootTableConfidence.None)
            return;

        HasNumbers = true;
        bool rated = tableConfidence is LootTableConfidence.Rated && item.Confidence is LootTableConfidence.Rated;
        ShowsRatePercentage = rated;
        RateText = rated
            ? $"{Percentage(item.DropRate)}"
            : $"seen in {item.RunCount.ToString(CultureInfo.InvariantCulture)} of {tableRuns.ToString(CultureInfo.InvariantCulture)}";
        RateBar = item.DropRate;
        RateTooltip = rated
            ? $"{item.RunCount.ToString(CultureInfo.InvariantCulture)} of {tableRuns.ToString(CultureInfo.InvariantCulture)} runs · 90 % interval {Percentage(item.DropRateLow)}–{Percentage(item.DropRateHigh)}"
            : tableConfidence is LootTableConfidence.Rated && item.IsRare
                ? $"{item.RunCount.ToString(CultureInfo.InvariantCulture)} of {tableRuns.ToString(CultureInfo.InvariantCulture)} runs — rare drops are counted in runs, not percentages"
                : $"{item.RunCount.ToString(CultureInfo.InvariantCulture)} of {tableRuns.ToString(CultureInfo.InvariantCulture)} runs — too few for a percentage";
        QuantityText = item.RunCount >= 3
            ? $"×{(item.AverageQuantity >= 10 ? item.AverageQuantity.ToString("0", CultureInfo.InvariantCulture) : item.AverageQuantity.ToString("0.0", CultureInfo.InvariantCulture))}"
            : item.MinimumQuantity == item.MaximumQuantity
                ? $"×{item.MinimumQuantity.ToString(CultureInfo.InvariantCulture)}"
                : $"×{item.MinimumQuantity.ToString(CultureInfo.InvariantCulture)}–{item.MaximumQuantity.ToString(CultureInfo.InvariantCulture)}";
        HasPrice = item.UnitPrice is not null;
        ValueText = item.UnitPrice is null ? "no price" : IskFormat.Compact(item.ValuePerDrop);
        ValueSub = item.UnitPrice is null
            ? string.Empty
            : item.RarityClass is LootRarityClass.Blueprint ? "build appraisal" : $"{IskFormat.Compact(item.UnitPrice.Value)} each";
        ShareBar = Math.Min(1d, item.ValueShare * BarScale);
        ShowsShareText = tableConfidence is LootTableConfidence.Rated;
        ShareText = Percentage(item.ValueShare);
        LastSeenText = item.LastSeenUtc is { } seen
            ? $"last seen {Math.Max(0, (int)(nowUtc - seen).TotalDays).ToString(CultureInfo.InvariantCulture)} d ago"
            : string.Empty;
    }

    public LootTableItemDto Item { get; }

    public string NameText { get; }

    public bool IsRare { get; }

    public string TagText { get; }

    public bool HasTag => TagText.Length > 0;

    public string ChangeText { get; }

    public bool HasChange => ChangeText.Length > 0;

    public bool HasNumbers { get; }

    public bool ShowsRatePercentage { get; }

    public string RateText { get; } = string.Empty;

    public double RateBar { get; }

    public string RateTooltip { get; } = string.Empty;

    public string QuantityText { get; } = string.Empty;

    public bool HasPrice { get; }

    public string ValueText { get; } = string.Empty;

    public string ValueSub { get; } = string.Empty;

    public double ShareBar { get; }

    public bool ShowsShareText { get; }

    public string ShareText { get; } = string.Empty;

    public string LastSeenText { get; } = string.Empty;

    [ObservableProperty] private Bitmap? _icon;

    /// <summary>The type's icon from the app's own image cache; the bundled placeholder where there is none (images off, offline).</summary>
    public async Task LoadIconAsync(ITypeImageProvider images) =>
        Icon = await images.GetImageAsync(Item.TypeId, TypeImageKind.Icon, 32) ?? TypeImagePlaceholder.Bitmap;

    public static string Percentage(double fraction) =>
        $"{Math.Round(fraction * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)} %";

    private static string _TagOf(LootRarityClass rarity) => rarity switch
    {
        LootRarityClass.Blueprint => "Blueprint",
        LootRarityClass.Faction => "Faction",
        LootRarityClass.Storyline => "Storyline",
        LootRarityClass.Deadspace => "Deadspace",
        LootRarityClass.Officer => "Officer",
        LootRarityClass.Seldom => "Rare",
        _ => string.Empty
    };
}
