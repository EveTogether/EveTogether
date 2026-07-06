namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// The fixed operator kernel (design §0): the canonical apply order and the classification of which operators take a
/// stacking penalty / are assignments. Stable EVE mechanics — changes only if CCP reworks dogma.
/// </summary>
public static class DogmaOperators
{
    private const int SkillLevelOperation = 9;   // skill-level-from-SP: skipped (level is a direct input)

    /// <summary>Operators in the order a value is computed (PreAssign -&gt; PostAssign).</summary>
    public static readonly EffectOperator[] ApplyOrder =
    [
        EffectOperator.PreAssign,
        EffectOperator.PreMul,
        EffectOperator.PreDiv,
        EffectOperator.ModAdd,
        EffectOperator.ModSub,
        EffectOperator.PostMul,
        EffectOperator.PostDiv,
        EffectOperator.PostPercent,
        EffectOperator.PostAssign
    ];

    private static readonly HashSet<EffectOperator> Penalizable =
    [
        EffectOperator.PreMul,
        EffectOperator.PreDiv,
        EffectOperator.PostMul,
        EffectOperator.PostDiv,
        EffectOperator.PostPercent
    ];

    /// <summary>True for the multiplicative operators eligible for the stacking penalty (never assignment/additive).</summary>
    public static bool IsPenalizable(EffectOperator op) => Penalizable.Contains(op);

    /// <summary>True for the assignment operators, which pick the min/max value on highIsGood rather than combining.</summary>
    public static bool IsAssignment(EffectOperator op) => op is EffectOperator.PreAssign or EffectOperator.PostAssign;

    /// <summary>Maps an SDE operation code to an operator; false for the skipped skill-level operation (9) or an
    /// unknown code.</summary>
    public static bool TryFromOperation(int operation, out EffectOperator op)
    {
        if (operation == SkillLevelOperation)
        {
            op = default;
            return false;
        }
        op = (EffectOperator)operation;
        return Enum.IsDefined(op);
    }
}
