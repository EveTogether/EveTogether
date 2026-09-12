using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-231, ET-256: the homefront payout counts as <see cref="IskCertainty.Expected"/> until the pilot confirms it,
/// and a run that already carries a confirmed <see cref="RunParameterKey.FixedPayout"/> row is never counted twice —
/// once here as expected and again by <see cref="RewardIskContributor"/> as measured.
/// </summary>
public sealed class HomefrontPayoutIskContributorTests
{
    [Fact]
    public void Contribute_UnconfirmedExpectedPayout_CountsAsExpected()
    {
        RunIskFacts facts = _Facts(expected: 15_000_000m);

        IskContribution? contribution = new HomefrontPayoutIskContributor().Contribute([facts], DateTime.UtcNow);

        Assert.NotNull(contribution);
        Assert.Equal(15_000_000m, contribution.Amount);
        Assert.Equal(IskCertainty.Expected, contribution.Certainty);
    }

    [Fact]
    public void Contribute_AlreadyConfirmed_IsSkipped_SoItIsNeverCountedTwice()
    {
        RunIskFacts facts = _Facts(expected: 15_000_000m,
            parameters: [new RunIskParameter(RunParameterKey.FixedPayout, 15_000_000m, null, DateTime.UtcNow)]);

        IskContribution? contribution = new HomefrontPayoutIskContributor().Contribute([facts], DateTime.UtcNow);

        Assert.Null(contribution);
    }

    [Fact]
    public void Contribute_SumsAcrossRuns_ButOnlyTheUnconfirmedOnes()
    {
        RunIskFacts confirmed = _Facts(expected: 15_000_000m,
            parameters: [new RunIskParameter(RunParameterKey.FixedPayout, 15_000_000m, null, DateTime.UtcNow)]);
        RunIskFacts stillExpected = _Facts(expected: 15_000_000m);

        IskContribution? contribution = new HomefrontPayoutIskContributor().Contribute([confirmed, stillExpected], DateTime.UtcNow);

        Assert.NotNull(contribution);
        Assert.Equal(15_000_000m, contribution.Amount);
        Assert.Equal(IskCertainty.Expected, contribution.Certainty);
    }

    [Fact]
    public void Contribute_NothingExpectedOnAnyRun_IsNull()
    {
        RunIskFacts facts = _Facts(expected: null);

        Assert.Null(new HomefrontPayoutIskContributor().Contribute([facts], DateTime.UtcNow));
    }

    private static RunIskFacts _Facts(decimal? expected, IReadOnlyList<RunIskParameter>? parameters = null) => new()
    {
        BountyIsk = 0m,
        LootIskNet = null,
        HasLoot = false,
        ConsumableIskCost = null,
        HasConsumables = false,
        MiningIskValue = null,
        HasMining = false,
        Parameters = parameters ?? [],
        StoppedAtUtc = null,
        HomefrontExpectedPayoutIsk = expected
    };
}
