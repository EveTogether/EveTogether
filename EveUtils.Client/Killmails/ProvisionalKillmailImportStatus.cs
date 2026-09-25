namespace EveUtils.Client.Killmails;

/// <summary>Outcome category of parsing pasted killmail clipboard text into a provisional killmail (ET-340).</summary>
public enum ProvisionalKillmailImportStatus
{
    Imported,

    /// <summary>The text carries no recognisable "Victim:" line — not a killmail paste at all.</summary>
    NotAKillmailText,

    /// <summary>The victim's ship name is not in the SDE — the one field this parser treats as a hard error
    /// rather than a warning (ET-340).</summary>
    UnknownShip,

    /// <summary>Neither the victim nor an attacker name in the text matches one of the pilot's own characters.</summary>
    NoOwnCharacter
}
