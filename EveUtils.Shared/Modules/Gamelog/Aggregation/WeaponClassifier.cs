using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Sde;

namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// Turret, drone or missile, from the weapon name an outgoing damage line carries (ET-277): the SDE category of the
/// type with that name, which is exact. Without a built SDE only a missile is recognised, by the words every
/// missile-type charge name carries — the one class that must never be given a percentage.
/// </summary>
public static class WeaponClassifier
{
    private const int ModuleCategory = 7;
    private const int ChargeCategory = 8;
    private const int DroneCategory = 18;
    private const int FighterCategory = 87;

    private static readonly string[] MissileWords = ["Missile", "Rocket", "Torpedo", "Bomb"];

    public static WeaponClass Classify(ISdeAccessor? sde, string weapon)
    {
        if (sde is { IsAvailable: true } && sde.TryGetTypeId(weapon, out var typeId)
            && sde.GetType(typeId) is { } type && sde.GetGroup(type.GroupId) is { } group)
        {
            return group.CategoryId switch
            {
                ModuleCategory => WeaponClass.Turret,
                ChargeCategory => WeaponClass.Missile,
                DroneCategory or FighterCategory => WeaponClass.Drone,
                _ => WeaponClass.Unknown,
            };
        }

        return MissileWords.Any(word => weapon.Contains(word, StringComparison.OrdinalIgnoreCase))
            ? WeaponClass.Missile
            : WeaponClass.Unknown;
    }
}
