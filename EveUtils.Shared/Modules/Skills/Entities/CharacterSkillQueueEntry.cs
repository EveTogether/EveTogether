namespace EveUtils.Shared.Modules.Skills.Entities;

/// <summary>
/// One entry of a character's training queue, cached client-side from ESI
/// (<c>GET /characters/{id}/skillqueue/</c>) for the read-only queue view. Unlike <c>CharacterSkill</c> (the merged
/// effective levels) this preserves the raw queue order and per-entry timing. Keyed by (CharacterId, QueuePosition);
/// position 0 is the skill currently training.
/// </summary>
public sealed class CharacterSkillQueueEntry
{
    public int CharacterId { get; set; }
    public int QueuePosition { get; set; }
    public int SkillTypeId { get; set; }
    public int FinishedLevel { get; set; }
    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? FinishDate { get; set; }
}
