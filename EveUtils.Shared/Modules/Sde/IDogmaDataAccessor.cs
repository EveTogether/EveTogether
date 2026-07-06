using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Sde;

/// <summary>
/// Read-only access to the dogma calculation data in the local SDE store: attribute metadata, per-type base
/// attributes and effects, and the parsed <c>modifierInfo</c> rules. A separate seam from <see cref="ISdeAccessor"/>
/// (which serves type/name/slot lookups for the fit parsers) but backed by the same memory-mapped SQLite file over
/// the same pooled connection. Thread-safe for concurrent reads; lookups are memoised per instance and each effect's
/// <c>modifierInfo</c> is parsed once. Call <see cref="PrefetchAsync"/> with a fit's full type set before a
/// calculation so the hot evaluation loop touches in-memory caches only (no IO). Call <see cref="Reopen"/> after the
/// importer swaps in a new build.
/// </summary>
public interface IDogmaDataAccessor
{
    /// <summary>Attribute metadata (default value, stackable, highIsGood), or null when the attribute is unknown.</summary>
    DogmaAttributeMeta? GetAttributeMeta(int attributeId);

    /// <summary>The type's base dogma attribute values (the calculation's starting point before modifiers).</summary>
    IReadOnlyList<SdeDogmaAttribute> GetBaseAttributes(int typeId);

    /// <summary>The effects a type carries (ship/module/skill/...), with their SDE default flag.</summary>
    IReadOnlyList<DogmaTypeEffect> GetTypeEffects(int typeId);

    /// <summary>An effect's activation category and parsed modifier rules, or null when the effect is unknown.</summary>
    DogmaEffectDef? GetEffect(int effectId);

    /// <summary>
    /// The category of a type (Type -&gt; Group -&gt; Category), used for the stacking-penalty exemption check: the
    /// exemption keys on the source <em>type</em>'s category, not on the attribute (V-1). Null when the type is unknown.
    /// </summary>
    int? GetCategoryId(int typeId);

    /// <summary>The group of a type, used to route <c>LocationGroupModifier</c> effects to items of a given group.
    /// Null when the type is unknown.</summary>
    int? GetGroupId(int typeId);

    /// <summary>Every published skill type id (category 16), so an all-level-5 fit can inject the whole skill set
    /// (skills carry the fitting/navigation/tanking bonuses, not just the modules' required skills).</summary>
    IReadOnlyList<int> GetSkillTypeIds();

    /// <summary>The type's mass (from the Type row, not dogma — ships carry mass there), or null when unknown. Needed
    /// for the propulsion-module speed formula, which divides by the ship's mass.</summary>
    double? GetMass(int typeId);

    /// <summary>
    /// A Tactical Destroyer's default (Defense) tactical-mode type id, or null when the ship is not a T3D. The mode
    /// is a group-1306 "Ship Modifiers" item carrying the per-stance bonuses (resists/signature/speed/range); an
    /// imported fit defaults to Defense, so the validation/calc uses it too.
    /// </summary>
    int? GetDefaultTacticalModeTypeId(int shipTypeId);

    /// <summary>All of a Tactical Destroyer's stance modes (typeId + name, ordered by typeId so Defense is first); empty
    /// for non-T3D ships. Drives the fit-detail mode selector.</summary>
    IReadOnlyList<SdeNamedType> GetTacticalModes(int shipTypeId);

    /// <summary>The type's cargo capacity (Type row), or null when unknown. Used to size a cap booster's charge clip.</summary>
    double? GetCapacity(int typeId);

    /// <summary>The type's volume (Type row), or null when unknown. With a module's capacity it sizes a charge clip.</summary>
    double? GetVolume(int typeId);

    /// <summary>
    /// Batch-load the dogma rows for every type in a fit (one query per table) so the subsequent calculation runs
    /// entirely against in-memory caches. Idempotent — already-cached types are skipped.
    /// </summary>
    Task PrefetchAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken = default);

    /// <summary>Re-point the accessor at the store file and drop all caches (call after an importer swap).</summary>
    void Reopen();
}
