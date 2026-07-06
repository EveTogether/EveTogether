using System.Text.Json.Serialization;

namespace EveUtils.Client.Skills;

/// <summary>One entry from ESI <c>GET /characters/{id}/skills/</c> — a trained skill snapshot.</summary>
public sealed class EsiSkill
{
    [JsonPropertyName("skill_id")] public int SkillId { get; set; }
    [JsonPropertyName("trained_skill_level")] public int TrainedSkillLevel { get; set; }
    [JsonPropertyName("active_skill_level")] public int ActiveSkillLevel { get; set; }
}
