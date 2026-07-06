namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// The input to a dogma calculation: a ship, its modules, the skill source, its drones in space and the
/// implants in the character's slots. <see cref="Drones"/> / <see cref="Implants"/> default to none so earlier call
/// sites are unaffected. <see cref="TacticalModeTypeId"/> overrides a Tactical Destroyer's stance (null = the default
/// Defense mode). <see cref="Profile"/> selects the incoming damage mix for weighted EHP; null
/// defaults to <see cref="DamageProfile.Uniform"/>, keeping backwards-compatible results.
/// </summary>
public sealed record FitInput(
    int ShipTypeId,
    IReadOnlyList<ModuleInput> Modules,
    SkillSource Skills,
    IReadOnlyList<DroneInput>? Drones = null,
    IReadOnlyList<ImplantInput>? Implants = null,
    int? TacticalModeTypeId = null,
    DamageProfile? Profile = null,
    WeatherInput? Weather = null,
    IReadOnlyList<FighterInput>? Fighters = null);
