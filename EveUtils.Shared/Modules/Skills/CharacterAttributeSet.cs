using EveUtils.Shared.Modules.Dogma;

namespace EveUtils.Shared.Modules.Skills;

/// <summary>
/// A character's five effective training attributes: base allocation plus attribute-implant bonuses. A skill's
/// SP/min reads its primary/secondary attribute from here by the SDE attribute id the skill points at (180/181).
/// </summary>
public sealed record CharacterAttributeSet(
    double Charisma, double Intelligence, double Memory, double Perception, double Willpower)
{
    // EVE's in-game *fitting* "Skills Required" panel does not use the logged-in character's real attributes/implants;
    // it estimates against a generic baseline of ~25 SP/min Omega (effectively ~16.7 per attribute, the game's minimum/
    // base). Verified: 8,083,328 SP / 7mo14d3h26m = 25.044 SP/min ⇒ primary + secondary/2 = 1.5·a ⇒ a ≈ 16.7. The real
    // skill queue uses the character's own attributes (our accurate default); this baseline only lets the UI optionally
    // match the in-game panel 1:1 for comparison.
    private const double FittingPanelAttribute = 16.7;

    /// <summary>The neutral baseline EVE's in-game fitting "Skills Required" panel estimates against (~25 SP/min Omega),
    /// independent of the character's real attributes and implants — for an optional 1:1 comparison with that panel.</summary>
    public static CharacterAttributeSet FittingPanelBaseline { get; } = new(
        FittingPanelAttribute, FittingPanelAttribute, FittingPanelAttribute, FittingPanelAttribute, FittingPanelAttribute);

    /// <summary>The effective value of the character attribute with the given SDE attribute id (164-168); 0 for an
    /// unknown id.</summary>
    public double For(int attributeId) => attributeId switch
    {
        DogmaAttributeIds.Charisma => Charisma,
        DogmaAttributeIds.Intelligence => Intelligence,
        DogmaAttributeIds.Memory => Memory,
        DogmaAttributeIds.Perception => Perception,
        DogmaAttributeIds.Willpower => Willpower,
        _ => 0
    };
}
