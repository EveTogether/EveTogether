namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// The fixed stacking-penalty kernel (design §0): the penalty factor, the exempt source categories and the per-rank
/// multiplier. The exemption keys on the source <em>type</em>'s category (V-1), not on the attribute. Const + pinned
/// in tests — these EVE constants change almost never; a config layer would be false flexibility.
/// </summary>
public static class DogmaPenalty
{
    /// <summary>The stacking penalty factor: 1 / exp((1 / 2.67)^2) ~= 0.87.</summary>
    public const double Factor = 0.8691199808003974;

    /// <summary>Source categories exempt from the stacking penalty: Ship(6), Charge(8), Skill(16), Implant(20), Subsystem(32).</summary>
    public static readonly IReadOnlySet<int> ExemptCategoryIds = new HashSet<int> { 6, 8, 16, 20, 32 };

    /// <summary>The penalty multiplier for the modifier at <paramref name="rank"/> (0 = strongest, unpenalised): Factor^(rank^2).</summary>
    public static double StackingMultiplier(int rank) => Math.Pow(Factor, rank * (double)rank);
}
