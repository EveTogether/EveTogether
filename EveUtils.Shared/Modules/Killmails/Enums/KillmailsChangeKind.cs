namespace EveUtils.Shared.Modules.Killmails.Enums;

/// <summary>What happened to stored killmails, carried by <c>KillmailsChangedEvent</c>. Local only, so new values may
/// be added freely.</summary>
public enum KillmailsChangeKind
{
    RunLinkChanged,

    /// <summary>New killmails were stored for the character (ET-383).</summary>
    Imported,

    /// <summary>A provisional killmail was added or replaced by the real mail for the character (ET-340).</summary>
    ProvisionalChanged
}
