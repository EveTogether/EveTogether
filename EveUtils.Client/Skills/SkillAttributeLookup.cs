using EveUtils.Shared.Modules.Dogma;
using EveUtils.Shared.Modules.Skills.Entities;

namespace EveUtils.Client.Skills;

/// <summary>Maps an SDE training-attribute id (164-168, <see cref="DogmaAttributeIds"/>) to a character's stored
/// value and its display name — shared by the SKILLS catalogue, queue and detail panes (ET-16) so the three read
/// the same five-way switch instead of three drifting copies of it.</summary>
public static class SkillAttributeLookup
{
    public static int Value(CharacterAttributes attributes, int attributeId) => attributeId switch
    {
        DogmaAttributeIds.Charisma => attributes.Charisma,
        DogmaAttributeIds.Intelligence => attributes.Intelligence,
        DogmaAttributeIds.Memory => attributes.Memory,
        DogmaAttributeIds.Perception => attributes.Perception,
        DogmaAttributeIds.Willpower => attributes.Willpower,
        _ => 0
    };

    public static string Name(int attributeId) => attributeId switch
    {
        DogmaAttributeIds.Charisma => "Charisma",
        DogmaAttributeIds.Intelligence => "Intelligence",
        DogmaAttributeIds.Memory => "Memory",
        DogmaAttributeIds.Perception => "Perception",
        DogmaAttributeIds.Willpower => "Willpower",
        _ => "?"
    };
}
