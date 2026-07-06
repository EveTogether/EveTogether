using EveUtils.Shared.Modules.Dogma;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Pins the operator kernel (design §0): the canonical apply order, the penalty/assignment classification and the
/// SDE operation-code mapping (including the skipped skill-level operation 9).
/// </summary>
public class DogmaOperatorsTests
{
    [Fact]
    public void ApplyOrder_IsCanonical_PreAssignFirst_PostAssignLast()
    {
        Assert.Equal(
            new[]
            {
                EffectOperator.PreAssign, EffectOperator.PreMul, EffectOperator.PreDiv,
                EffectOperator.ModAdd, EffectOperator.ModSub,
                EffectOperator.PostMul, EffectOperator.PostDiv, EffectOperator.PostPercent, EffectOperator.PostAssign
            },
            DogmaOperators.ApplyOrder);
    }

    [Theory]
    [InlineData(EffectOperator.PreMul, true)]
    [InlineData(EffectOperator.PreDiv, true)]
    [InlineData(EffectOperator.PostMul, true)]
    [InlineData(EffectOperator.PostDiv, true)]
    [InlineData(EffectOperator.PostPercent, true)]
    [InlineData(EffectOperator.ModAdd, false)]
    [InlineData(EffectOperator.ModSub, false)]
    [InlineData(EffectOperator.PreAssign, false)]
    [InlineData(EffectOperator.PostAssign, false)]
    public void IsPenalizable_OnlyMultiplicativeOperators(EffectOperator op, bool expected) =>
        Assert.Equal(expected, DogmaOperators.IsPenalizable(op));

    [Theory]
    [InlineData(EffectOperator.PreAssign, true)]
    [InlineData(EffectOperator.PostAssign, true)]
    [InlineData(EffectOperator.PostMul, false)]
    [InlineData(EffectOperator.ModAdd, false)]
    public void IsAssignment_OnlyAssignOperators(EffectOperator op, bool expected) =>
        Assert.Equal(expected, DogmaOperators.IsAssignment(op));

    [Theory]
    [InlineData(-1, EffectOperator.PreAssign)]
    [InlineData(4, EffectOperator.PostMul)]
    [InlineData(6, EffectOperator.PostPercent)]
    [InlineData(7, EffectOperator.PostAssign)]
    public void TryFromOperation_MapsKnownCodes(int operation, EffectOperator expected)
    {
        Assert.True(DogmaOperators.TryFromOperation(operation, out var op));
        Assert.Equal(expected, op);
    }

    [Theory]
    [InlineData(9)]    // skill-level-from-SP: intentionally skipped
    [InlineData(8)]    // undefined code
    [InlineData(42)]
    public void TryFromOperation_RejectsSkippedAndUnknown(int operation) =>
        Assert.False(DogmaOperators.TryFromOperation(operation, out _));
}
