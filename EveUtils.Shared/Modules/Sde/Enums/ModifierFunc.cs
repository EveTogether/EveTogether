namespace EveUtils.Shared.Modules.Sde.Enums;

/// <summary>
/// The function of a single <see cref="EveUtils.Shared.Modules.Sde.Dtos.ModifierInfo"/> entry — decides how the modifier finds its target item(s).
/// Mirrors the CCP SDE string values verbatim. <see cref="Unknown"/> guards against a future SDE introducing a
/// function we do not yet handle: the engine skips it rather than mis-applying it (data-driven, no recompile needed
/// when CCP adds modules, but a brand-new func code stays inert until we map it).
/// </summary>
public enum ModifierFunc
{
    Unknown = 0,
    ItemModifier,
    LocationModifier,
    LocationGroupModifier,
    LocationRequiredSkillModifier,
    OwnerRequiredSkillModifier,
    EffectStopper
}
