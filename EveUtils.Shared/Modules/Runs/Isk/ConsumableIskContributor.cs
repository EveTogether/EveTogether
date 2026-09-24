using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>CONSUMABLES: what a run spent — its abyssal filament and whatever else its pilot wrote out (ET-249, ET-334)
/// — contributed as negative ISK so TOTAL ISK is a true net. A run that spent something with no price yet adds nothing
/// and says so, the same rule LOOT follows, rather than passing for a cost that came out at zero.</summary>
internal sealed class ConsumableIskContributor : IIskContributor
{
    public IskSource Source => IskSource.Consumables;

    public IskContribution? Contribute(IReadOnlyList<RunIskFacts> runs, DateTime nowUtc)
    {
        if (runs.Any(run => run.ConsumableIskCost is not null))
            return new IskContribution(Source, -runs.Sum(run => run.ConsumableIskCost.GetValueOrDefault()), IskCertainty.Measured);

        return runs.Any(run => run.HasConsumables) ? new IskContribution(Source, 0m, IskCertainty.Unknown) : null;
    }
}
