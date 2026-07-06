using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// Orchestrates the four passes behind <see cref="IDogmaCalculator"/>: batch-prefetch the fit's types (so the
/// calculation does no IO), build the object graph, collect effects, then hand back a <see cref="FitResult"/> that
/// resolves attributes on demand. The evaluator is shared with the derived-stats pass so memoisation is consistent.
/// </summary>
public sealed class DogmaCalculator(
    IDogmaDataAccessor data,
    DogmaFitBuilder builder,
    DogmaEffectCollector collector,
    ReactiveArmorHardener reactiveArmorHardener,
    DerivedStatsCalculator derivedStats,
    DogmaEvaluator evaluator) : IDogmaCalculator, ISingletonService
{
    public async Task<FitResult> CalculateAsync(FitInput fit, CancellationToken cancellationToken = default)
    {
        await data.PrefetchAsync(TypeIds(fit), cancellationToken);
        var graph = builder.Build(fit);
        collector.Collect(graph);
        // The Reactive Armor Hardener adapts to the resolved armour resonances, so it runs after collection and folds
        // its result back in before the derived (EHP) pass reads them.
        reactiveArmorHardener.Apply(graph);
        return new FitResult(derivedStats.Calculate(graph, fit.Profile), derivedStats.CalculateContributions(graph), graph, evaluator,
            derivedStats.CalculateFighterContributions(graph));
    }

    private IReadOnlyCollection<int> TypeIds(FitInput fit)
    {
        var ids = new HashSet<int> { fit.ShipTypeId };
        foreach (var module in fit.Modules)
        {
            ids.Add(module.TypeId);
            if (module.ChargeTypeId is { } chargeTypeId)
                ids.Add(chargeTypeId);
        }
        foreach (var drone in fit.Drones ?? [])
            ids.Add(drone.TypeId);
        foreach (var fighter in fit.Fighters ?? [])
            ids.Add(fighter.TypeId);
        foreach (var skillId in fit.Skills.InjectsAllSkills ? data.GetSkillTypeIds() : fit.Skills.ExplicitSkillTypeIds)
            ids.Add(skillId);
        return ids;
    }
}
