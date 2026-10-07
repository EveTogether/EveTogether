using System.Globalization;
using System.Linq;
using EveUtils.Client.Formatting;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels;

/// <summary>One name of the unrecognised items log (ET-460).</summary>
public sealed class UnrecognisedItemRowViewModel(UnrecognisedLootItemDto item)
{
    public string Name => item.Name;

    public string QuantityText => $"{item.TotalQuantity.ToString("N0", CultureInfo.CurrentCulture)}×";

    public string RunsText => item.RunCount switch
    {
        0 => "no run",
        1 => "1 run",
        var count => $"{count} runs"
    };

    public string FirstSeenText => $"first seen {item.FirstSeenAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

    public string SourcesText => string.Join(" · ", item.Sources.Select(source => source switch
    {
        UnrecognisedItemSource.ClipboardCapture => "clipboard copy",
        UnrecognisedItemSource.RunWindowEntry => "run window",
        UnrecognisedItemSource.Appraisal => "appraisal",
        _ => source.ToString()
    }));

    public bool IsResolved => item.ResolvedAtUtc is not null;

    public string ResolvedText => item.ResolvedAtUtc is { } resolvedAt
        ? $"recognised {resolvedAt.ToLocalTime():yyyy-MM-dd HH:mm} as type {item.ResolvedTypeId}, "
          + $"{(item.ResolvedUnitPrice is { } price ? $"{IskFormat.WholeOrNoPrice(price)} each then" : "no price then")}"
        : string.Empty;
}
