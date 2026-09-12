using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>LOOT: priced loot gained less priced loot lost, which can run negative. A run whose loot nobody can price
/// yet adds nothing and says so, rather than passing for a valuation that came out at zero (ET-217).</summary>
internal sealed class LootIskContributor : IIskContributor
{
    public IskSource Source => IskSource.Loot;

    public IskContribution? Contribute(IReadOnlyList<RunIskFacts> runs, DateTime nowUtc)
    {
        if (runs.Any(run => run.LootIskNet is not null))
            return new IskContribution(Source, runs.Sum(run => run.LootIskNet.GetValueOrDefault()), IskCertainty.Measured);

        return runs.Any(run => run.HasLoot) ? new IskContribution(Source, 0m, IskCertainty.Unknown) : null;
    }
}
