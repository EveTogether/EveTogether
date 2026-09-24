namespace EveUtils.Client.Killmails;

/// <summary>Outcome category of a character killmail import.</summary>
public enum KillmailImportStatus
{
    Imported,
    ScopeMissing,
    AuthRequired,
    Failed
}
