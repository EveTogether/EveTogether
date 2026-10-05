namespace EveUtils.Client.Killmails;

/// <summary>The result of importing pasted killmail clipboard text: status, how many own characters got a
/// provisional row, and a message.</summary>
public sealed record ProvisionalKillmailImportResult(ProvisionalKillmailImportStatus Status, int StoredCount, string? Message = null)
{
    public bool IsSuccess => Status == ProvisionalKillmailImportStatus.Imported;

    public static ProvisionalKillmailImportResult Ok(int count) => new(ProvisionalKillmailImportStatus.Imported, count);
}
