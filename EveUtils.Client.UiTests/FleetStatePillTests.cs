using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Modules.Fleet.Entities;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// A fleet's activation state lives on its own ACTIVE / FORMING pill (amber while forming), distinct
/// from the green participation dot (<see cref="FleetViewModel.IsActive"/> = "the fleet I'm currently in").
/// </summary>
public class FleetStatePillTests
{
    private static FleetViewModel Row(FleetActivation activation) =>
        new(new FleetInfo(1, "Test", null, FleetVisibility.Public, FleetState.Active, 42,
            FromTime: null, ToTime: null, CreatedAt: default, activation), actingCharacterId: 42);

    [Fact]
    public void StatePill_Forming_IsAmberLabelled()
    {
        var vm = Row(FleetActivation.Forming);
        Assert.Equal("FORMING", vm.StateLabel);
        Assert.True(vm.IsForming);
    }

    [Fact]
    public void StatePill_Active_IsAccentLabelled()
    {
        var vm = Row(FleetActivation.Active);
        Assert.Equal("ACTIVE", vm.StateLabel);
        Assert.False(vm.IsForming);
    }
}
