namespace EveUtils.Client.Killmails;

/// <summary>Outcome category of a character killmail import.</summary>
public enum KillmailImportStatus
{
    Imported,
    ScopeMissing,
    AuthRequired,

    /// <summary>Neither the victim nor any attacker is one of the pilot's own characters (ET-338) — nothing was
    /// stored, and the one ESI call already made was public and free.</summary>
    NoOwnCharacter,
    Failed
}
