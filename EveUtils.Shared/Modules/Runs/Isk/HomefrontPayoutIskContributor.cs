using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>
/// A homefront's fixed payout, counted the moment the site reads Completed (ET-269) — never "expected until
/// confirmed": there is no wallet scope to check it against and never will be, so the table's own figure for a
/// ticked character counts as <see cref="IskCertainty.Measured"/> straight away, the same as a measured bounty line.
///
/// A pilot who typed a different figure because something else really arrived already carries a
/// <see cref="RunParameterKey.FixedPayout"/> row for that run, which <see cref="RewardIskContributor"/> counts
/// instead — this contributor skips exactly that run, so the two never both count it.
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
                continue; // A typed correction on this run — RewardIskContributor counts that instead.

            sum += expected;
            any = true;
        }

        return any ? new IskContribution(Source, sum, IskCertainty.Measured) : null;
    }
}
