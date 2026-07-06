namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// One fighter type's own resolved readout for the per-squadron tooltip (the fighter counterpart of
/// <see cref="ModuleContribution"/>): the burst DPS of a single fighter (× a squadron's active fighter count gives that
/// squadron's DPS) and the engagement envelope. Computed on the same resolve paths as the aggregate FighterDps — one per
/// distinct launched type, since the per-fighter values are identical across squadrons of a type. The ranges are
/// informational (the sim is target-less): the attack ability's optimal/falloff and the secondary salvo's flight range,
/// 0 when the ability is absent.
/// </summary>
public sealed record FighterContribution(
    int TypeId,
    double DpsPerFighter,
    double OptimalRange,
    double FalloffRange,
    double SalvoRange,
    FighterEwar? Ewar = null);
