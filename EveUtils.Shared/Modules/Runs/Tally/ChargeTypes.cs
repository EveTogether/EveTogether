using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Runs.Tally;

/// <summary>Which types are charges (missiles, turret ammo), by their SDE category, for <see cref="LootTally"/>'s split
/// (ET-471).</summary>
public static class ChargeTypes
{
    private const int ChargeCategory = 8;

    // Without a built SDE nothing is a charge: the run then counts as it did before the split, never on a guess.
    public static Func<int, bool> Of(ISdeAccessor? sde) => typeId =>
        sde is { IsAvailable: true } && sde.GetType(typeId) is { } type
        && sde.GetGroup(type.GroupId)?.CategoryId == ChargeCategory;
}
