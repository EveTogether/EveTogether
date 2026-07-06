using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Modules.Fleet.Entities;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Regression for the fleet-row status label (2026-06-04 GUI test): a Concluded fleet showed "Forming" in the
/// My Fleets/Browser row because <see cref="FleetViewModel"/> mapped only Active vs Forming, so Concluded fell into
/// the else. Every activation must surface its own label in both <c>ActivationLabel</c> and the combined StatusLabel.
/// </summary>
public class FleetRowActivationLabelTests
{
    private static FleetInfo Info(FleetActivation activation) => new(
        Id: 1, Name: "Test", Description: null, Visibility: FleetVisibility.Public, State: FleetState.Active,
        CreatorCharacterId: 42, FromTime: null, ToTime: null, CreatedAt: default, Activation: activation);

    [Theory]
    [InlineData(FleetActivation.Forming, "Forming")]
    [InlineData(FleetActivation.Active, "Active")]
    [InlineData(FleetActivation.Concluded, "Concluded")]
    public void ActivationLabel_and_StatusLabel_reflect_every_activation(FleetActivation activation, string expected)
    {
        var row = new FleetViewModel(Info(activation), actingCharacterId: 42);

        Assert.Equal(expected, row.ActivationLabel);
        Assert.StartsWith("Public", row.StatusLabel);
        Assert.EndsWith(expected, row.StatusLabel);
    }
}
