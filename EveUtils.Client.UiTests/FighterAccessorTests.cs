using EveUtils.Shared.Modules.Sde.Fighters;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// the fighter read-model derivation. Kind and the structure flag come from the inventory group (the Standup
/// variants carry no IsLight/IsSupport/IsHeavy flags), squadron size/role/deals-damage from the dogma attributes, and a
/// platform's launchable set is filtered by its per-kind tube limits (light 2217 / support 2218 / heavy 2219).
/// </summary>
public class FighterAccessorTests
{
    private const int LightGroup = 1652, SupportGroup = 1537, HeavyGroup = 1653, StructureLightGroup = 4777;
    private const int FighterCategory = 87, ShipCategory = 6, StructureCategory = 65;
    private const int SquadronMaxSize = 2215, SquadronRole = 2270, AttackMultiplier = 2226;
    private const int Tubes = 2216, LightSlots = 2217, SupportSlots = 2218, HeavySlots = 2219;

    // A carrier (light + support tubes, no heavy), a supercarrier (light + heavy, no support), a structure platform,
    // one fighter of each kind, a Standup variant and a non-fighter ship.
    private static FighterAccessor Build()
    {
        var sde = new FakeSdeAccessor()
            .Add(24483, "Nidhoggur", 547, ShipCategory)
            .Attr(24483, LightSlots, 3).Attr(24483, SupportSlots, 2)                  // carrier: light + support
            .Add(23913, "Nyx", 659, ShipCategory)
            .Attr(23913, LightSlots, 3).Attr(23913, SupportSlots, 0).Attr(23913, HeavySlots, 4)  // super: light + heavy
            .Add(35832, "Astrahus", 1657, StructureCategory)
            .Attr(35832, Tubes, 3)                                                    // structure: total tubes, no per-kind limits
            .Add(587, "Rifter", 25, ShipCategory)                                      // non-fighter platform
            .Add(23055, "Templar I", LightGroup, FighterCategory, volume: 1000)
            .Attr(23055, SquadronMaxSize, 6).Attr(23055, SquadronRole, 2).Attr(23055, AttackMultiplier, 1)
            .Add(32325, "Cyclops I", HeavyGroup, FighterCategory, volume: 1800)
            .Attr(32325, SquadronMaxSize, 6).Attr(32325, SquadronRole, 4).Attr(32325, AttackMultiplier, 1)
            .Add(37599, "Cenobite I", SupportGroup, FighterCategory, volume: 3000)
            .Attr(37599, SquadronMaxSize, 3).Attr(37599, SquadronRole, 3)             // support: no attack multiplier
            .Add(47035, "Standup Templar I", StructureLightGroup, FighterCategory, volume: 2000)
            .Attr(47035, SquadronMaxSize, 9).Attr(47035, SquadronRole, 2).Attr(47035, AttackMultiplier, 1);
        return new FighterAccessor(sde);
    }

    [Fact]
    public void GetFighterType_ClassifiesLightAttack_FromGroupAndAttributes()
    {
        var fighter = Build().GetFighterType(23055);

        Assert.NotNull(fighter);
        Assert.Equal(FighterKind.Light, fighter!.Kind);
        Assert.False(fighter.IsStructureFighter);
        Assert.Equal(6, fighter.SquadronMaxSize);
        Assert.Equal(FighterRole.LightAttack, fighter.Role);
        Assert.Equal(1000, fighter.Volume);
        Assert.True(fighter.DealsDamage);
    }

    [Fact]
    public void GetFighterType_SupportSquadron_DealsNoDamage()
    {
        var fighter = Build().GetFighterType(37599);

        Assert.NotNull(fighter);
        Assert.Equal(FighterKind.Support, fighter!.Kind);
        Assert.Equal(FighterRole.Support, fighter.Role);
        Assert.False(fighter.DealsDamage);   // no attack multiplier (2226)
    }

    [Fact]
    public void GetFighterType_StandupVariant_IsStructureFighter_FromGroup()
    {
        var fighter = Build().GetFighterType(47035);

        Assert.NotNull(fighter);
        Assert.Equal(FighterKind.Light, fighter!.Kind);   // classified by group, not the (absent) IsLight flag
        Assert.True(fighter.IsStructureFighter);
    }

    [Fact]
    public void GetFighterType_NonFighterType_ReturnsNull()
    {
        Assert.Null(Build().GetFighterType(587));   // Rifter — not a fighter group
    }

    [Fact]
    public void ListLaunchableFighters_Carrier_LightAndSupport_ExcludesHeavyAndStandup()
    {
        var launchable = Build().ListLaunchableFighters(24483).Select(f => f.TypeId).ToList();

        Assert.Contains(23055, launchable);    // Templar (light)
        Assert.Contains(37599, launchable);    // Cenobite (support)
        Assert.DoesNotContain(32325, launchable);   // Cyclops (heavy) — carrier has no heavy tube
        Assert.DoesNotContain(47035, launchable);   // Standup — only structures launch those
    }

    [Fact]
    public void ListLaunchableFighters_Supercarrier_LightAndHeavy_ExcludesSupport()
    {
        var launchable = Build().ListLaunchableFighters(23913).Select(f => f.TypeId).ToList();

        Assert.Contains(23055, launchable);    // Templar (light)
        Assert.Contains(32325, launchable);    // Cyclops (heavy)
        Assert.DoesNotContain(37599, launchable);   // Cenobite (support) — Nyx support limit is 0
    }

    [Fact]
    public void ListLaunchableFighters_Structure_OnlyStandupFighters()
    {
        var launchable = Build().ListLaunchableFighters(35832).Select(f => f.TypeId).ToList();

        Assert.Equal([47035], launchable);     // only the Standup light fighter, never the ship Templar
    }

    [Fact]
    public void ListLaunchableFighters_NonFighterPlatform_Empty()
    {
        Assert.Empty(Build().ListLaunchableFighters(587));   // Rifter has no fighter tubes
    }
}
