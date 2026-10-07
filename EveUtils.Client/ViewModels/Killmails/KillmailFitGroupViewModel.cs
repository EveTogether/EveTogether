namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>One slot group in the killmail detail screen's FIT section (ET-333) — HULL, HIGH SLOTS, CARGO and so
/// on, with its item count and the ISK that dropped and was destroyed in it (ET-475).</summary>
public sealed class KillmailFitGroupViewModel(
    string header, string countText, string droppedText, string destroyedText,
    IReadOnlyList<KillmailDetailItemRowViewModel> rows)
{
    public string Header { get; } = header;

    public string CountText { get; } = countText;

    public string DroppedText { get; } = droppedText;

    public string DestroyedText { get; } = destroyedText;

    public IReadOnlyList<KillmailDetailItemRowViewModel> Rows { get; } = rows;
}
