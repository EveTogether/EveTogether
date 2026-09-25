using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Skills.Plans.Enums;

namespace EveUtils.Shared.Modules.Skills.Plans.Events;

/// <summary>
/// A character's skill plan changed: created, renamed, deleted, or its rows changed (ET-355). Published on the local
/// bus once the write is done, so the PLANS tab re-reads however the change was made. Never on the wire: a plan is
/// local-only data and never sent anywhere by itself (D-179).
/// </summary>
public sealed class SkillPlansChangedEvent(int characterId, SkillPlansChangeKind kind, int planId)
    : IntegrationEvent<SkillPlansChangedData>(new SkillPlansChangedData(characterId, kind, planId))
{
    public override string EventType => "skillplans.changed";
}

public sealed record SkillPlansChangedData(int CharacterId, SkillPlansChangeKind Kind, int PlanId);
