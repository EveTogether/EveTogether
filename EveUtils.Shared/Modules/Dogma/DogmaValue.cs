namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// One attribute slot on a <see cref="DogmaItem"/>: its base value, the modifiers registered against it (pass 2) and
/// the memoised resolved value (pass 3). A forced value (e.g. a skill level set directly) bypasses the modifier
/// pipeline entirely.
/// </summary>
public sealed class DogmaValue
{
    private readonly List<Modifier> _modifiers = [];

    public DogmaValue(double baseValue, bool isForced = false)
    {
        BaseValue = baseValue;
        IsForced = isForced;
    }

    public double BaseValue { get; }

    /// <summary>A directly-set value that bypasses modifiers (skill levels, design §3 pass 1).</summary>
    public bool IsForced { get; }

    /// <summary>The memoised pass-3 result, or null until first evaluated.</summary>
    public double? Resolved { get; set; }

    /// <summary>Guard against a self-referential attribute cycle during evaluation (EVE dogma is acyclic in practice;
    /// if a value is read while already resolving it falls back to its base).</summary>
    public bool Resolving { get; set; }

    public IReadOnlyList<Modifier> Modifiers => _modifiers;

    public void AddModifier(in Modifier modifier) => _modifiers.Add(modifier);
}
