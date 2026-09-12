using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>
/// A homefront's fixed payout while it is still owed rather than paid (ET-231): the curve's own figure for a ticked
/// character, counted as <see cref="IskCertainty.Expected"/> so TOTAL ISK says a part of it is owed rather than
/// arrived (<see cref="IskBreakdown.HasExpectedPart"/>).
///
/// A run the pilot has confirmed or typed an actual amount for already carries a
/// <see cref="RunParameterKey.FixedPayout"/> row, which <see cref="RewardIskContributor"/> counts as
/// <see cref="IskCertainty.Measured"/> — this contributor skips exactly that run, so the two never both count it.
/// </summary>
internal sealed class HomefrontPayoutIskContributor : IIskContributor
{
    public IskSource Source => IskSource.HomefrontPayout;

    public IskContribution? Contribute(IReadOnlyList<RunIskFacts> runs, DateTime nowUtc)
    {
        decimal sum = 0m;
        bool any = false;
        foreach (RunIskFacts run in runs)
        {
            if (run.HomefrontExpectedPayoutIsk is not { } expected)
                continue;
            if (run.Parameters.Any(parameter => parameter.Key == RunParameterKey.FixedPayout))
                continue; // Already confirmed — RewardIskContributor counts this run's payout as measured instead.

            sum += expected;
            any = true;
        }

        return any ? new IskContribution(Source, sum, IskCertainty.Expected) : null;
    }
}
