namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// The dogma engine's public entry point: takes a fit and returns its resolved stats. Prefetches the fit's data,
/// builds the graph (pass 1), collects effects (pass 2), then evaluates on demand (pass 3) for the derived stats
/// (pass 4). Lives on the client; host-agnostic.
/// </summary>
public interface IDogmaCalculator
{
    Task<FitResult> CalculateAsync(FitInput fit, CancellationToken cancellationToken = default);
}
