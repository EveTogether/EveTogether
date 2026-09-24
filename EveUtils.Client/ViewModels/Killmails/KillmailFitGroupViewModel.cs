namespace EveUtils.Client.ViewModels.Killmails;

/// <summary>One slot group in the killmail detail screen's FIT section (ET-333) — SHIP, HIGH SLOTS, CARGO and so
/// on, each with its own item count and value summary.</summary>
public sealed class KillmailFitGroupViewModel(string header, string summaryText, IReadOnlyList<KillmailDetailItemRowViewModel> rows)
{
    public string Header { get; } = header;

    public string SummaryText { get; } = summaryText;

    public IReadOnlyList<KillmailDetailItemRowViewModel> Rows { get; } = rows;
}
