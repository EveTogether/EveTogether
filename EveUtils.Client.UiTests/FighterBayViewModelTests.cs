using System.Linq;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Modules.Sde.Fighters;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// the Fighter Bay simulation logic — loading reserves into tubes, unloading, the per-kind tube limits and the
/// total tube bound, the per-squadron active-fighter clamp, and the launched set the DPS engine reads. Pure VM logic, no
/// UI thread.
/// </summary>
public class FighterBayViewModelTests
{
    private static FighterSquadronViewModel Squad(int typeId, FighterKind kind, int size = 6, double volume = 1000) =>
        new(new FighterType(typeId, $"Fighter {typeId}", kind, IsStructureFighter: false, size, FighterRole.LightAttack, volume, DealsDamage: true));

    // A carrier: 4 tubes, light limit 3, support limit 2, no heavy.
    private static FighterBayViewModel Carrier() => new(tubeCount: 4, lightLimit: 3, supportLimit: 2, heavyLimit: 0, bayCapacity: 70000);

    // A supercarrier: 5 tubes, light limit 3, no support, heavy limit 4.
    private static FighterBayViewModel Supercarrier() => new(tubeCount: 5, lightLimit: 3, supportLimit: 0, heavyLimit: 4, bayCapacity: 110000);

    [Fact]
    public void Seed_FillsTubesToKindLimit_OverflowToReserve()
    {
        var bay = Carrier();
        for (var i = 0; i < 4; i++)
            bay.Seed(Squad(100 + i, FighterKind.Light), launched: true);   // 4 light, limit 3

        Assert.Equal(3, bay.Tubes.Count(tube => tube.Squadron is not null));
        Assert.Single(bay.Reserves);                                       // the 4th light could not be launched
        Assert.Equal("3/3", bay.LightLabel);
    }

    [Fact]
    public void LoadFighter_Blocked_WhenKindLimitReached()
    {
        var bay = Carrier();
        for (var i = 0; i < 3; i++)
            bay.Seed(Squad(100 + i, FighterKind.Light), launched: true);   // light limit reached
        var reserveLight = Squad(200, FighterKind.Light);
        bay.Seed(reserveLight, launched: false);

        bay.LoadFighterCommand.Execute(reserveLight);

        Assert.False(reserveLight.IsLaunched);
        Assert.Contains(reserveLight, bay.Reserves);
        Assert.Equal("3/3", bay.LightLabel);
    }

    [Fact]
    public void LoadFighter_MovesReserveIntoTube_WhenKindHasRoom()
    {
        var bay = Carrier();
        var support = Squad(300, FighterKind.Support);
        bay.Seed(support, launched: false);

        bay.LoadFighterCommand.Execute(support);

        Assert.True(support.IsLaunched);
        Assert.DoesNotContain(support, bay.Reserves);
        Assert.Equal("1/2", bay.SupportLabel);
    }

    [Fact]
    public void TotalTubeCount_BoundsLaunched_EvenWhenKindLimitAllowsMore()
    {
        var bay = Supercarrier();                                          // 5 tubes; heavy limit 4
        for (var i = 0; i < 3; i++)
            bay.Seed(Squad(100 + i, FighterKind.Light), launched: true);   // 3 light → 3 tubes
        for (var i = 0; i < 4; i++)
            bay.Seed(Squad(200 + i, FighterKind.Heavy), launched: true);   // 4 heavy, but only 2 tubes left

        Assert.Equal(5, bay.Tubes.Count(tube => tube.Squadron is not null));
        Assert.Equal(2, bay.Reserves.Count);                              // 4 heavy − 2 launched = 2 reserves
        Assert.Equal("2/4", bay.HeavyLabel);                              // heavy limit not the binding constraint
    }

    [Fact]
    public void UnloadFighter_ReturnsSquadronToReserve()
    {
        var bay = Carrier();
        var light = Squad(100, FighterKind.Light);
        bay.Seed(light, launched: true);

        bay.UnloadFighterCommand.Execute(light);

        Assert.False(light.IsLaunched);
        Assert.Contains(light, bay.Reserves);
        Assert.All(bay.Tubes, tube => Assert.Null(tube.Squadron));
    }

    [Fact]
    public void IncreaseActive_CappedAtSquadronSize()
    {
        var bay = Carrier();
        var light = Squad(100, FighterKind.Light, size: 6);
        bay.Seed(light, launched: true);
        Assert.Equal(6, light.ActiveCount);                               // launches at full strength

        bay.IncreaseActiveCommand.Execute(light);
        Assert.Equal(6, light.ActiveCount);                               // capped at the squadron size
    }

    [Fact]
    public void DecreaseActive_FloorsAtZero_AndKeepsSquadronLaunched()
    {
        var bay = Carrier();
        var light = Squad(100, FighterKind.Light, size: 6);
        bay.Seed(light, launched: true);

        for (var i = 0; i < 10; i++)
            bay.DecreaseActiveCommand.Execute(light);

        Assert.Equal(0, light.ActiveCount);   // floored at zero
        Assert.True(light.IsLaunched);        // "-" never unloads — only the ✕ does
    }

    [Fact]
    public void LaunchedFighters_ReflectsTubeSquadrons_WithActiveCount()
    {
        var bay = Carrier();
        var light = Squad(100, FighterKind.Light, size: 6);
        var reserve = Squad(101, FighterKind.Light);
        bay.Seed(light, launched: true);
        bay.Seed(reserve, launched: false);
        bay.DecreaseActiveCommand.Execute(light);                         // 6 → 5 active

        var launched = bay.LaunchedFighters;

        Assert.Single(launched);                                          // the reserve is not launched
        Assert.Equal(100, launched[0].TypeId);
        Assert.Equal(5, launched[0].ActiveCount);
    }

    [Fact]
    public void Seed_StructureWithoutPerKindLimits_LaunchesUpToTotalTubes()
    {
        // An Upwell structure: a total tube count but no per-kind limits (light/support/heavy all 0).
        var bay = new FighterBayViewModel(tubeCount: 3, lightLimit: 0, supportLimit: 0, heavyLimit: 0, bayCapacity: 200000);
        for (var i = 0; i < 4; i++)
            bay.Seed(Squad(100 + i, FighterKind.Light, size: 9), launched: true);   // 4 Standup squadrons, 3 tubes

        Assert.Equal(3, bay.Tubes.Count(tube => tube.Squadron is not null));   // any kind fills tubes up to the total...
        Assert.Single(bay.Reserves);                                          // ...the 4th waits in the bay
    }

    [Fact]
    public void RemoveAll_ClearsTubesAndReserves()
    {
        var bay = Carrier();
        bay.Seed(Squad(100, FighterKind.Light), launched: true);
        bay.Seed(Squad(300, FighterKind.Support), launched: false);

        bay.RemoveAllCommand.Execute(null);

        Assert.All(bay.Tubes, tube => Assert.Null(tube.Squadron));
        Assert.Empty(bay.Reserves);
        Assert.False(bay.HasFighters);
    }
}
