using System;

namespace EveUtils.Shared.Modules.Skills.Entities;

/// <summary>
/// A character's five training attributes, cached client-side from ESI (<c>GET /characters/{id}/attributes/</c>) —
/// the values ESI reports, which already include attribute implants (<see cref="CharacterAttributeResolver"/>
/// recovers the base allocation for remapping). One row per character (keyed by <see cref="CharacterId"/>).
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

    /// <summary>When the character last remapped, from ESI <c>last_remap_date</c>. Null when never remapped.</summary>
    public DateTimeOffset? LastRemapDate { get; set; }

    /// <summary>When the next free remap accrues, from ESI <c>accrued_remap_cooldown_date</c>. Null when ESI has
    /// not reported it — the OPTIMISE tab shows "unknown" rather than guessing a date (ET-354 D7/A4).</summary>
    public DateTimeOffset? AccruedRemapCooldownDate { get; set; }

    /// <summary>Remap tokens banked ahead of the cooldown, from ESI <c>bonus_remaps</c>.</summary>
    public int? BonusRemaps { get; set; }
}
