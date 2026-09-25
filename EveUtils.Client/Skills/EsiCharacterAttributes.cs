using System;
using System.Text.Json.Serialization;

namespace EveUtils.Client.Skills;

/// <summary>The ESI <c>GET /characters/{id}/attributes/</c> response: the character's five training attributes
/// (effective values, implants included) plus the three remap-cooldown fields, stored for the OPTIMISE tab's remap
/// advice (ET-354 D7 — previously discarded as data-minimalisation; that call is superseded now they have a use).</summary>
public sealed class EsiCharacterAttributes
{
    [JsonPropertyName("charisma")] public int Charisma { get; set; }
    [JsonPropertyName("intelligence")] public int Intelligence { get; set; }
    [JsonPropertyName("memory")] public int Memory { get; set; }
    [JsonPropertyName("perception")] public int Perception { get; set; }
    [JsonPropertyName("willpower")] public int Willpower { get; set; }
    [JsonPropertyName("last_remap_date")] public DateTimeOffset? LastRemapDate { get; set; }
    [JsonPropertyName("accrued_remap_cooldown_date")] public DateTimeOffset? AccruedRemapCooldownDate { get; set; }
    [JsonPropertyName("bonus_remaps")] public int? BonusRemaps { get; set; }
}
