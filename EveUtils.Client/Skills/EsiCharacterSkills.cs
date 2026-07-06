using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EveUtils.Client.Skills;

/// <summary>The ESI <c>GET /characters/{id}/skills/</c> response envelope (the snapshot of trained skills).</summary>
public sealed class EsiCharacterSkills
{
    [JsonPropertyName("skills")] public List<EsiSkill> Skills { get; set; } = [];
}
