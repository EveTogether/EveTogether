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
}
