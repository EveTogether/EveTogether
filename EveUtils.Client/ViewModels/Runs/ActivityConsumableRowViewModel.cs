using EveUtils.Client.Formatting;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>One character's own confirmed filament count on the detail screen's CONSUMABLES (ET-249) — read-only,
/// unlike the run window's editable row: a saved activity's count is whatever was confirmed at SAVE.</summary>
public sealed class ActivityConsumableRowViewModel(string characterText, int count, decimal? value)
{
    public string CharacterText { get; } = characterText;

    public string CountText { get; } = $"{count}x";

    public string ValueText { get; } = value is { } isk ? IskFormat.Whole(isk) : "no figure yet";
}
