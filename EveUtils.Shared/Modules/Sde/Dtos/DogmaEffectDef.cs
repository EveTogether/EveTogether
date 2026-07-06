namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>
/// A dogma effect reduced to what the engine needs: its activation state (<see cref="EffectCategoryId"/> — 0 passive
/// / 1 active / 4 online / 5 overload), its parsed <see cref="Modifiers"/>, and its SDE <see cref="Name"/> (used to
/// recognise booster side-effects, which are off by default). Effects without a <c>modifierInfo</c> array carry an
/// empty modifier list; synthetic patch effects carry an empty name.
/// </summary>
public sealed record DogmaEffectDef(
    int EffectId, int EffectCategoryId, IReadOnlyList<ModifierInfo> Modifiers, string Name = "");
