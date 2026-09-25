using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Skills.Events;

/// <summary>
/// A character's imported skills, training queue or attributes changed — an ESI (re-)import, whether from
/// <see cref="EveUtils.Client.Skills.SkillRefreshService"/>'s background poll or an on-demand call (ET-387).
/// Published on the local bus once the write is done, so an open SKILLS window re-reads without the pilot pressing
/// F5. Local-only: a skill import is never shared or relayed anywhere.
/// </summary>
public sealed class SkillsChangedEvent(int characterId)
    : IntegrationEvent<SkillsChangedData>(new SkillsChangedData(characterId))
{
    public override string EventType => "skills.changed";
}

public sealed record SkillsChangedData(int CharacterId);
