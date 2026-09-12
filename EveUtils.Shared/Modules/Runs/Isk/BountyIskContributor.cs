using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>BOUNTY: what the gamelog paid out, per run, added up over the activity.</summary>
internal sealed class BountyIskContributor : IIskContributor
{
    public IskSource Source => IskSource.Bounty;

    // Zero is "no payout came past", which is no line at all rather than a figure of its own — the same reading every
    // total in the app gave a bounty of zero before this contributor existed.
    public IskContribution? Contribute(IReadOnlyList<RunIskFacts> runs, DateTime nowUtc)
    {
        decimal bounty = runs.Sum(run => run.BountyIsk);
        return bounty == 0m ? null : new IskContribution(Source, bounty, IskCertainty.Measured);
    }
}
