using System.Linq;
using EveUtils.Shared.Modules.Sde.Storage;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Covers the synthetic abyssal weather beacons: the reconstructed definitions (group 1983 is SDE-empty) and
/// that the Dogma data accessor resolves them through the synthetic path, reusing the real category-7 system effects.
/// The magnitudes are EVE University values (validate in-game); the effect/attribute plumbing is taken from live beacons.
/// </summary>
public class AbyssalBeaconsTests
{
    [Fact]
    public void All_FourResistWeathers_TimesThreePenaltyStrengths()
    {
        Assert.Equal(12, AbyssalBeacons.All.Count);   // Electrical/Exotic/Firestorm/Gamma × {−30,−50,−70%}; Dark deferred
        foreach (var beacon in AbyssalBeacons.All)
        {
            // a flat +50% bonus attribute plus the resist penalty on all three tank layers = 4 attributes / 4 effects
            Assert.Equal(4, beacon.BaseAttributes.Count);
            Assert.Equal(4, beacon.EffectIds.Count);
            Assert.Equal(AbyssalBeacons.Category, beacon.Category);
        }
        Assert.Equal(AbyssalBeacons.All.Count, AbyssalBeacons.All.Select(b => b.TypeId).Distinct().Count());
    }

    [Fact]
    public void Gamma50_HasShieldHpBonusAndExplosiveResistPenaltyOnEveryLayer()
    {
        var gamma = AbyssalBeacons.All.Single(beacon => beacon.DisplayName == "Abyssal Gamma (−50% resist)");
        // +50% shield HP via the real systemShieldHP effect (3992) reading shieldCapacityMultiplier (146).
        Assert.Contains(gamma.BaseAttributes, a => a.AttributeId == 146 && a.Value == 1.50);
        Assert.Contains(3992, gamma.EffectIds);
        // −50% explosive resist on armor (1468/3997), shield (1490/4136) and hull (985/8078); positive value = penalty.
        Assert.Contains(gamma.BaseAttributes, a => a.AttributeId == 1468 && a.Value == 50);
        Assert.Contains(gamma.BaseAttributes, a => a.AttributeId == 1490 && a.Value == 50);
        Assert.Contains(gamma.BaseAttributes, a => a.AttributeId == 985 && a.Value == 50);
        Assert.Contains(3997, gamma.EffectIds);
        Assert.Contains(4136, gamma.EffectIds);
        Assert.Contains(8078, gamma.EffectIds);
    }

    [Fact]
    public void DogmaDataAccessor_ResolvesSyntheticBeacon_WithoutAStore()
    {
        // No store on disk -> only the synthetic patch path can answer for an abyssal beacon; a real id falls through.
        var data = new SqliteDogmaDataAccessor("/nonexistent/sde.sqlite");
        var gamma = AbyssalBeacons.All.Single(beacon => beacon.DisplayName == "Abyssal Gamma (−50% resist)");

        Assert.Equal(AbyssalBeacons.GroupId, data.GetGroupId(gamma.TypeId));
        Assert.Equal(AbyssalBeacons.CategoryId, data.GetCategoryId(gamma.TypeId));
        Assert.Contains(data.GetBaseAttributes(gamma.TypeId), a => a.AttributeId == 146 && a.Value == 1.50);
        Assert.Contains(data.GetTypeEffects(gamma.TypeId), effect => effect.EffectId == 3992);
        Assert.Empty(data.GetBaseAttributes(587));   // non-synthetic id, no store -> empty, no crash
    }
}
