using System.Globalization;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One charge a run's two cargo holds show was fired (ET-471), with the game log's count of hits for it as a
/// check. That count is never a figure: one hit line is not shown to be one charge.</summary>
public sealed class SpentChargeLineViewModel(ActivityLootLineViewModel line, int? gamelogHits)
{
    public ActivityLootLineViewModel Line { get; } = line;

    public string? GamelogText { get; } = gamelogHits is { } hits
        ? $"game log: {hits.ToString("N0", CultureInfo.CurrentCulture)} hits"
        : null;
}
