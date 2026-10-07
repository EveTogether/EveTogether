namespace EveUtils.Shared.Modules.Killmails.Enums;

/// <summary>What happened to stored killmails, carried by <c>KillmailsChangedEvent</c>. Local only, so new values may
/// be added freely.</summary>
public enum KillmailsChangeKind
{
    RunLinkChanged,

    /// <summary>New killmails were stored for the character (ET-383).</summary>
    Imported,

    /// <summary>A provisional killmail was added or replaced by the real mail for the character (ET-340).</summary>
    ProvisionalChanged,

    /// <summary>A fleet mate's shared killmails were stored or withdrawn under their character (ET-371). Deliberately
    /// not <see cref="Imported"/>: that one means "your own new mails" to the Local API and to fleet sharing.</summary>
    FleetShareChanged,

    /// <summary>A stored mail got its victim position from ESI (ET-473).</summary>
    PositionFilled
}
