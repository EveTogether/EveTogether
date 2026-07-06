using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Client.UiTests;

/// <summary>
/// In-memory <see cref="IDogmaDataAccessor"/> for the pass 1/2 routing tests — fluent setup, no SQLite. Lets a test
/// declare exactly the types, attributes and effects it needs and assert how modifiers route, without the real store.
/// </summary>
internal sealed class FakeDogmaDataAccessor : IDogmaDataAccessor
{
    private readonly Dictionary<int, DogmaAttributeMeta> _attributes = new();
    private readonly Dictionary<int, List<SdeDogmaAttribute>> _baseAttributes = new();
    private readonly Dictionary<int, List<DogmaTypeEffect>> _typeEffects = new();
    private readonly Dictionary<int, DogmaEffectDef> _effects = new();
    private readonly Dictionary<int, int> _groupId = new();
    private readonly Dictionary<int, int> _categoryId = new();
    private readonly Dictionary<int, double> _mass = new();
    private readonly Dictionary<int, double> _capacity = new();
    private readonly Dictionary<int, double> _volume = new();
    private readonly Dictionary<int, int> _tacticalMode = new();
    private readonly Dictionary<int, IReadOnlyList<SdeNamedType>> _tacticalModes = new();

    public FakeDogmaDataAccessor Attribute(
        int attributeId, double defaultValue, bool stackable, bool highIsGood = true, int? maxAttributeId = null)
    {
        _attributes[attributeId] = new DogmaAttributeMeta(attributeId, defaultValue, stackable, highIsGood, maxAttributeId);
        return this;
    }

    public FakeDogmaDataAccessor Type(int typeId, int groupId, int categoryId, params SdeDogmaAttribute[] baseAttributes)
    {
        _groupId[typeId] = groupId;
        _categoryId[typeId] = categoryId;
        _baseAttributes[typeId] = baseAttributes.ToList();
        return this;
    }

    public FakeDogmaDataAccessor TypeEffect(int typeId, int effectId)
    {
        if (!_typeEffects.TryGetValue(typeId, out var effects))
            _typeEffects[typeId] = effects = [];
        effects.Add(new DogmaTypeEffect(effectId, IsDefault: true));
        return this;
    }

    public FakeDogmaDataAccessor Effect(int effectId, int effectCategoryId, params ModifierInfo[] modifiers)
    {
        _effects[effectId] = new DogmaEffectDef(effectId, effectCategoryId, modifiers);
        return this;
    }

    public FakeDogmaDataAccessor EffectNamed(int effectId, string name, int effectCategoryId, params ModifierInfo[] modifiers)
    {
        _effects[effectId] = new DogmaEffectDef(effectId, effectCategoryId, modifiers, name);
        return this;
    }

    public FakeDogmaDataAccessor Mass(int typeId, double mass)
    {
        _mass[typeId] = mass;
        return this;
    }

    public FakeDogmaDataAccessor Cargo(int typeId, double capacity, double volume)
    {
        _capacity[typeId] = capacity;
        _volume[typeId] = volume;
        return this;
    }

    public FakeDogmaDataAccessor TacticalMode(int shipTypeId, int modeTypeId)
    {
        _tacticalMode[shipTypeId] = modeTypeId;
        return this;
    }

    public FakeDogmaDataAccessor TacticalModes(int shipTypeId, params SdeNamedType[] modes)
    {
        _tacticalModes[shipTypeId] = modes;
        return this;
    }

    public DogmaAttributeMeta? GetAttributeMeta(int attributeId) => _attributes.GetValueOrDefault(attributeId);

    public IReadOnlyList<SdeDogmaAttribute> GetBaseAttributes(int typeId) =>
        _baseAttributes.GetValueOrDefault(typeId) ?? [];

    public IReadOnlyList<DogmaTypeEffect> GetTypeEffects(int typeId) => _typeEffects.GetValueOrDefault(typeId) ?? [];

    public DogmaEffectDef? GetEffect(int effectId) => _effects.GetValueOrDefault(effectId);

    public int? GetCategoryId(int typeId) => _categoryId.TryGetValue(typeId, out var value) ? value : null;

    public int? GetGroupId(int typeId) => _groupId.TryGetValue(typeId, out var value) ? value : null;

    public IReadOnlyList<int> GetSkillTypeIds() =>
        _categoryId.Where(entry => entry.Value == 16).Select(entry => entry.Key).ToList();

    public double? GetMass(int typeId) => _mass.TryGetValue(typeId, out var value) ? value : null;

    public int? GetDefaultTacticalModeTypeId(int shipTypeId) =>
        _tacticalMode.TryGetValue(shipTypeId, out var value) ? value : null;

    public IReadOnlyList<SdeNamedType> GetTacticalModes(int shipTypeId) =>
        _tacticalModes.TryGetValue(shipTypeId, out var modes) ? modes : [];

    public double? GetCapacity(int typeId) => _capacity.TryGetValue(typeId, out var value) ? value : null;

    public double? GetVolume(int typeId) => _volume.TryGetValue(typeId, out var value) ? value : null;

    public Task PrefetchAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void Reopen() { }
}
