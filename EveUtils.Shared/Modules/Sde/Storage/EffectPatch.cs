using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Sde.Storage;

/// <summary>
/// Modifiers the engine supplies for an effect the SDE ships with an empty <c>modifierInfo</c> (a CCP omission
/// patched here), or a brand-new effect (custom id &gt; 45000). Applied by
/// <see cref="SqliteDogmaDataAccessor"/>: when the SDE effect exists but carries no modifiers its <see cref="Modifiers"/>
/// are used (the SDE category is kept); when there is no SDE row the effect is built from this patch. A real SDE effect
/// with modifiers is never overridden.
/// </summary>
public sealed record EffectPatch(int EffectId, int EffectCategoryId, IReadOnlyList<ModifierInfo> Modifiers);
