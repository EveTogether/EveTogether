using EveUtils.Shared.Modules.Dogma;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Pins the stacking-penalty kernel (design §0): the penalty factor, the exempt source-category set (Ship/Charge/
/// Skill/Implant/Subsystem) and the per-rank multiplier Factor^(rank^2). A single drift here is a silent calculation
/// error, so the values are asserted literally.
/// </summary>
public class DogmaPenaltyTests
{
    [Fact]
    public void Factor_IsPinned() => Assert.Equal(0.8691199808003974, DogmaPenalty.Factor);

    [Fact]
    public void ExemptCategoryIds_AreExactlyShipChargeSkillImplantSubsystem()
    {
        Assert.Equal(new HashSet<int> { 6, 8, 16, 20, 32 }, DogmaPenalty.ExemptCategoryIds);
        Assert.DoesNotContain(7, DogmaPenalty.ExemptCategoryIds);   // Module is penalised (VC-01)
    }

    [Fact]
    public void StackingMultiplier_RankZero_IsUnpenalised() =>
        Assert.Equal(1.0, DogmaPenalty.StackingMultiplier(0));

    [Fact]
    public void StackingMultiplier_RankOne_IsFactor() =>
        Assert.Equal(DogmaPenalty.Factor, DogmaPenalty.StackingMultiplier(1), 12);

    [Fact]
    public void StackingMultiplier_RankTwo_IsFactorToTheFourth() =>
        Assert.Equal(Math.Pow(DogmaPenalty.Factor, 4), DogmaPenalty.StackingMultiplier(2), 12);
}
