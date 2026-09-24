using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Enums;

namespace EveUtils.Shared.Modules.Killmails.Events;

/// <summary>
/// A character's stored killmails changed: a loss was linked to a run or unlinked from one (ET-382), or new mails were
/// stored (ET-383). Published on the local bus once the write is done, so the killmail screens re-read however the
/// change was made. Never on the wire: it describes this machine's database.
/// </summary>
public sealed class KillmailsChangedEvent(int characterId, KillmailsChangeKind kind)
    : IntegrationEvent<KillmailsChangedData>(new KillmailsChangedData(characterId, kind))
{
    public override string EventType => "killmails.changed";
}

public sealed record KillmailsChangedData(int CharacterId, KillmailsChangeKind Kind);
