namespace EveUtils.Shared.Modules.Dogma;

/// <summary>
/// A dogma modifier operator, declared in canonical apply order (PreAssign first, PostAssign last). The integer
/// values match the SDE <c>operation</c> codes so an operation int casts straight to the enum. Operation 9
/// (skill-level-from-SP) is intentionally absent — skill levels are a direct input, not calculated
/// (see <see cref="DogmaOperators.TryFromOperation"/>).
/// </summary>
public enum EffectOperator
{
    PreAssign = -1,
    PreMul = 0,
    PreDiv = 1,
    ModAdd = 2,
    ModSub = 3,
    PostMul = 4,
    PostDiv = 5,
    PostPercent = 6,
    PostAssign = 7
}
