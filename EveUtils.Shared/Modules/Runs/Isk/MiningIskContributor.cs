using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>MINING: priced ore mined, valued through the price source (Mutanite at its fixed NPC price, ET-229). A
/// run whose mining nobody can price yet adds nothing and says so, the same rule LOOT and CONSUMABLES follow, rather
/// than passing for a valuation that came out at zero.</summary>
internal sealed class MiningIskContributor : IIskContributor
{
    public IskSource Source => IskSource.Mining;

    public IskContribution? Contribute(IReadOnlyList<RunIskFacts> runs, DateTime nowUtc)
    {
        if (runs.Any(run => run.MiningIskValue is not null))
            return new IskContribution(Source, runs.Sum(run => run.MiningIskValue.GetValueOrDefault()), IskCertainty.Measured);

        return runs.Any(run => run.HasMining) ? new IskContribution(Source, 0m, IskCertainty.Unknown) : null;
    }
}
