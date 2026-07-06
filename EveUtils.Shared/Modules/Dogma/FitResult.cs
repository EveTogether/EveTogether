namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// The outcome of a dogma calculation: the headline <see cref="Derived"/> stats, the per-module
/// <see cref="Contributions"/>, plus on-demand access to any resolved ship attribute (for stats outside
/// <see cref="DerivedStats"/> or for tests). Backed by the evaluated graph, so attribute reads are memoised.
/// </summary>
public sealed class FitResult(
    DerivedStats derived, IReadOnlyList<ModuleContribution> contributions, DogmaFit fit, DogmaEvaluator evaluator,
    IReadOnlyList<FighterContribution>? fighterContributions = null)
{
    public DerivedStats Derived => derived;

    /// <summary>Each fitted module's and drone's own resolved contribution: modules in fit order, then drones.</summary>
    public IReadOnlyList<ModuleContribution> Contributions => contributions;

    /// <summary>Per distinct launched fighter type: the per-fighter DPS + range for the per-squadron tooltip.</summary>
    public IReadOnlyList<FighterContribution> FighterContributions => fighterContributions ?? [];

    public double ShipAttribute(int attributeId) => evaluator.Resolve(fit.Ship, attributeId);
}
