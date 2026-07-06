namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// Where a fit's implants come from: the implants configured on the fit itself, or a specific character's
/// actual plugged-in implants (ESI <c>esi-clones.read_implants.v1</c> import). Mirrors <see cref="SkillSource"/>: the
/// caller picks the mode, the provider materialises it into <see cref="ImplantInput"/>s before building the
/// <see cref="FitInput"/>. Implant bonuses then flow through the normal effect routing.
/// </summary>
public sealed record ImplantSource
{
    private readonly IReadOnlyList<int>? _characterTypeIds; // null => use the fit's own implants

    private ImplantSource(IReadOnlyList<int>? characterTypeIds) => _characterTypeIds = characterTypeIds;

    /// <summary>Use the implants configured on the fit itself (the simulator overlay), not a character's.</summary>
    public static ImplantSource FromFit { get; } = new((IReadOnlyList<int>?)null);

    /// <summary>Use a specific character's actual implant type ids (from an ESI import).</summary>
    public static ImplantSource FromCharacter(IReadOnlyList<int> implantTypeIds) => new(implantTypeIds);

    /// <summary>True when the fit's own implants should be used; false when a character's implants are selected.</summary>
    public bool UsesFitImplants => _characterTypeIds is null;

    /// <summary>The selected character's implant type ids; empty when <see cref="UsesFitImplants"/> is true.</summary>
    public IReadOnlyList<int> CharacterTypeIds => _characterTypeIds ?? [];
}
