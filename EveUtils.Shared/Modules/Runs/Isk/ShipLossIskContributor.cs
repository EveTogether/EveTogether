using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>SHIP LOSS: what the run's linked losses cost (ET-331), contributed as negative ISK like CONSUMABLES, so
/// TOTAL ISK stays a true net. A loss with no price yet adds nothing and says so.</summary>
internal sealed class ShipLossIskContributor : IIskContributor
{
    public IskSource Source => IskSource.ShipLoss;

    public IskContribution? Contribute(IReadOnlyList<RunIskFacts> runs, DateTime nowUtc)
    {
        if (runs.Any(run => run.ShipLossIskCost is not null))
        {
            return new IskContribution(Source, -runs.Sum(run => run.ShipLossIskCost.GetValueOrDefault()), IskCertainty.Measured);
        }

        return runs.Any(run => run.HasShipLoss) ? new IskContribution(Source, 0m, IskCertainty.Unknown) : null;
    }
}
