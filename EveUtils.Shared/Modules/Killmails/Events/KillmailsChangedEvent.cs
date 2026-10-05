using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Enums;

namespace EveUtils.Shared.Modules.Killmails.Events;

/// <summary>
/// A character's stored killmails changed: a loss was linked to a run or unlinked from one (ET-382), or new mails were
/// stored (ET-383). Published on the local bus once the write is done, so the killmail screens re-read however the
/// change was made. Never on the wire: it describes this machine's database.
/// </summary>
public sealed class KillmailsChangedEvent(int characterId, KillmailsChangeKind kind, IReadOnlyList<int>? addedKillmailIds = null)
    : IntegrationEvent<KillmailsChangedData>(new KillmailsChangedData(characterId, kind, addedKillmailIds ?? []))
{
    public override string EventType => "killmails.changed";
}

/// <param name="AddedKillmailIds">The mails this change actually stored; empty for a run-link change or when every
/// mail was already known.</param>
public sealed record KillmailsChangedData(int CharacterId, KillmailsChangeKind Kind, IReadOnlyList<int> AddedKillmailIds);
