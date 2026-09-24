using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EveUtils.Client.Skills;

/// <summary>The ESI <c>GET /characters/{id}/skills/</c> response envelope (the snapshot of trained skills).</summary>
public sealed class EsiCharacterSkills
{
    [JsonPropertyName("skills")] public List<EsiSkill> Skills { get; set; } = [];

    /// <summary>Total skill points across every trained skill — the SKILLS module header (ET-16) shows this
    /// figure directly rather than summing per-level SP, so it matches ESI exactly.</summary>
    [JsonPropertyName("total_sp")] public long TotalSp { get; set; }

    /// <summary>Skill points earned but not yet allocated to a skill. ESI omits this field for a character with
    /// none, which System.Text.Json leaves at the default (0) rather than distinguishing "none" from "zero"
    /// (ET-16 does not need that distinction).</summary>
    [JsonPropertyName("unallocated_sp")] public int UnallocatedSp { get; set; }
}
