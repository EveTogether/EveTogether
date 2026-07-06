using EveUtils.Shared.Modules.Sde.Enums;

namespace EveUtils.Shared.Modules.Sde.Dtos;

/// <summary>
/// One parsed entry of a dogma effect's <c>modifierInfo</c> array: a single data-driven rule that mutates a target
/// attribute. The CCP SDE carries no per-modifier value — the magnitude is read from the source item's
/// <see cref="ModifyingAttributeId"/> at calculation time. <see cref="ModifierFunc.EffectStopper"/> entries carry
/// only a domain and func (no operation, no attributes); their <see cref="Operation"/> is <see cref="NoOperation"/>.
/// </summary>
public sealed record ModifierInfo(
    ModifierFunc Func,
    ModifierDomain Domain,
    int Operation,
    int? ModifiedAttributeId,
    int? ModifyingAttributeId,
    int? GroupId,
    int? SkillTypeId)
{
    /// <summary>Sentinel <see cref="Operation"/> for entries without one (e.g. <see cref="ModifierFunc.EffectStopper"/>).</summary>
    public const int NoOperation = int.MinValue;
}
