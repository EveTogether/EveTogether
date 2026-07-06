namespace EveUtils.Shared.Modules.Skills.Entities;

/// <summary>
/// A character's effective trained level for one skill, cached client-side after an ESI import. "Effective"
/// merges the skills snapshot with the skill-queue: the snapshot lags behind the last in-game session, so any queue
/// entry already finished since the snapshot counts as trained. Keyed by (CharacterId, SkillTypeId).
/// </summary>
public sealed class CharacterSkill
{
    public int CharacterId { get; set; }
    public int SkillTypeId { get; set; }
    public int Level { get; set; }
}
