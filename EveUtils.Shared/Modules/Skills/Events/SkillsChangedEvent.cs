using EveUtils.Shared.Messaging;

namespace EveUtils.Shared.Modules.Skills.Events;

/// <summary>
/// A character's imported skills, training queue or attributes changed through an ESI (re-)import, background poll or
/// on-demand (ET-387). Published on the local bus after the write so an open SKILLS window re-reads; local-only, never
/// shared or relayed.
/// </summary>
public sealed class SkillsChangedEvent(int characterId)
    : IntegrationEvent<SkillsChangedData>(new SkillsChangedData(characterId))
{
    public override string EventType => "skills.changed";
}

public sealed record SkillsChangedData(int CharacterId);
