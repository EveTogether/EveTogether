namespace EveUtils.Client.Killmails;

/// <summary>The result of importing a character's killmails: status, how many new mails were stored, and a message.</summary>
public sealed record KillmailImportResult(KillmailImportStatus Status, int ImportedCount, string? Message = null)
{
    public bool IsSuccess => Status == KillmailImportStatus.Imported;

    public static KillmailImportResult Ok(int count) => new(KillmailImportStatus.Imported, count);
}
