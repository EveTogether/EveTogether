using EveUtils.Shared.Modules.Sde.Dtos;

namespace EveUtils.Shared.Modules.Sde.Storage;

/// <summary>
/// A synthetic environment "Effect Beacon" the engine supplies on top of the SDE (custom type id &gt; 45000), for the
/// abyssal weather that CCP ships without dogma (group 1983 is empty — the in-site bonus/penalty is applied server-side,
/// research §2). Modelled exactly like a real wormhole/metaliminal beacon: <see cref="BaseAttributes"/> carry the
/// multiplier/bonus values and <see cref="EffectIds"/> reference the real single-purpose category-7 system effects (e.g.
/// 3992 systemShieldHP) that apply them — so the beacon flows through the unchanged resolve, reusing proven modifierInfo.
/// Looked up by <see cref="SqliteDogmaDataAccessor"/> (engine data) and surfaced by <see cref="SqliteSdeAccessor"/>
/// (the picker). <see cref="DisplayName"/>/<see cref="Category"/>/<see cref="SortOrder"/> drive the picker entry.
/// </summary>
public sealed record SyntheticBeacon(
    int TypeId,
    string DisplayName,
    string Category,
    int SortOrder,
    IReadOnlyList<SdeDogmaAttribute> BaseAttributes,
    IReadOnlyList<int> EffectIds);
