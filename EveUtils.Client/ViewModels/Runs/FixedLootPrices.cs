using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>The unit price per type one run's own lines fixed (ET-463), earliest capture first — the rule the stored
/// summary values the run by, read off the captures a screen was handed, so the table and the total above it agree.</summary>
internal static class FixedLootPrices
{
    public static Dictionary<int, decimal> Of(IEnumerable<RunLootCaptureDto> captures)
    {
        Dictionary<int, decimal> prices = [];
        foreach (RunLootEntryDto entry in captures.OrderBy(capture => capture.CapturedAtUtc).SelectMany(capture => capture.Entries))
            if (entry.UnitPriceIsk is { } price)
                prices.TryAdd(entry.ItemTypeId, price);
        return prices;
    }
}
