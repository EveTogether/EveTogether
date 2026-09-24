namespace EveUtils.Shared.Modules.Skills.Entities;

/// <summary>
/// A character's five training attributes, cached client-side from ESI
/// (<c>GET /characters/{id}/attributes/</c>) — the base allocation <em>without</em> implants. Combined with the
/// character's attribute implants they give the effective attributes that drive the SP/min training rate. One row per
/// character (keyed by <see cref="CharacterId"/>).
/// </summary>
public sealed class CharacterAttributes
{
    public int CharacterId { get; set; }
    public int Charisma { get; set; }
    public int Intelligence { get; set; }
    public int Memory { get; set; }
    public int Perception { get; set; }
    public int Willpower { get; set; }

    /// <summary>The character's total skill points, from ESI <c>GET /characters/{id}/skills/</c>
    /// (<c>total_sp</c>) — the SKILLS module header (ET-16). Stored here rather than recomputed by summing
    /// trained levels, so it matches ESI exactly instead of drifting from rounding in the level formula.</summary>
    public long TotalSp { get; set; }

    /// <summary>Unallocated skill points from the same ESI response (<c>unallocated_sp</c>).</summary>
    public int UnallocatedSp { get; set; }
}
