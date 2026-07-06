using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// One entity in a fit's object graph (the ship, the character, a module/charge/drone/implant/skill). Holds its
/// state and a lazy attribute map seeded from the SDE base attributes; pass 2 registers modifiers and pass 3 fills
/// the memoised resolved values. Thrown away per fit (no cross-fit reuse).
/// </summary>
public sealed class DogmaItem
{
    private readonly Dictionary<int, DogmaValue> _attributes = new();

    public DogmaItem(
        int typeId, ModuleState state, int groupId, int categoryId, bool isAlwaysOn,
        IEnumerable<SdeDogmaAttribute> baseAttributes)
    {
        TypeId = typeId;
        State = state;
        GroupId = groupId;
        CategoryId = categoryId;
        IsAlwaysOn = isAlwaysOn;
        foreach (var attribute in baseAttributes)
            _attributes[attribute.AttributeId] = new DogmaValue(attribute.Value);
    }

    public int TypeId { get; }

    public ModuleState State { get; }

    /// <summary>The type's group, for <c>LocationGroupModifier</c> targeting.</summary>
    public int GroupId { get; }

    /// <summary>The type's category, for the stacking-penalty exemption check when this item is a modifier source.</summary>
    public int CategoryId { get; }

    /// <summary>Ships/characters/skills are always-on: their effects apply regardless of state (modules are gated).</summary>
    public bool IsAlwaysOn { get; }

    /// <summary>The charge loaded into this module, if any (the <c>otherID</c> target of the module's modifiers).</summary>
    public DogmaItem? Charge { get; set; }

    /// <summary>The module this charge is loaded into, if this item is a charge (the reverse <c>otherID</c> link).</summary>
    public DogmaItem? Host { get; set; }

    /// <summary>How many of this item are present — only meaningful for drones, where DPS scales with the count.</summary>
    public int Quantity { get; set; } = 1;

    public IReadOnlyDictionary<int, DogmaValue> Attributes => _attributes;

    /// <summary>The attribute slot, creating it from <paramref name="defaultValue"/> when the type does not carry it.</summary>
    public DogmaValue GetOrAdd(int attributeId, double defaultValue)
    {
        if (!_attributes.TryGetValue(attributeId, out var value))
        {
            value = new DogmaValue(defaultValue);
            _attributes[attributeId] = value;
        }
        return value;
    }

    public bool TryGet(int attributeId, out DogmaValue value) => _attributes.TryGetValue(attributeId, out value!);

    /// <summary>Set an attribute to a forced value that bypasses the modifier pipeline (e.g. a skill level).</summary>
    public void Force(int attributeId, double value) => _attributes[attributeId] = new DogmaValue(value, isForced: true);
}
