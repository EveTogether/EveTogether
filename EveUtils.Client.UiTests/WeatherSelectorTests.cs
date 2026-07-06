using System.Linq;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Shared.Modules.Sde.Storage;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Covers the weather/environment layer's data side: the group-920 name classifier (which beacons are kept and
/// how they are labelled/ordered) and the <see cref="WeatherSelectorViewModel"/> built on top of it.
/// </summary>
public class WeatherSelectorTests
{
    [Theory]
    [InlineData(30844, "Class 1 Pulsar Effects", "Pulsar C1", EnvironmentBeaconClassifier.Wormhole)]
    [InlineData(30869, "Class 6 Pulsar Effects", "Pulsar C6", EnvironmentBeaconClassifier.Wormhole)]
    [InlineData(30849, "Class 1 Wolf Rayet Effects", "Wolf-Rayet C1", EnvironmentBeaconClassifier.Wormhole)]
    [InlineData(56057, "Strong Metaliminal Electrical Storm", "Electrical Storm (Strong)", EnvironmentBeaconClassifier.Metaliminal)]
    [InlineData(56064, "Weak Metaliminal Electrical Storm", "Electrical Storm (Weak)", EnvironmentBeaconClassifier.Metaliminal)]
    [InlineData(52249, "Triglavian Invasion Strong System Effects", "Triglavian Invasion (Strong)", EnvironmentBeaconClassifier.Triglavian)]
    public void Classify_CuratedPhenomena_GetLabelAndCategory(int typeId, string name, string display, string category)
    {
        var beacon = EnvironmentBeaconClassifier.Classify(typeId, name);
        Assert.NotNull(beacon);
        Assert.Equal(typeId, beacon!.TypeId);
        Assert.Equal(display, beacon.DisplayName);
        Assert.Equal(category, beacon.Category);
    }

    [Theory]
    [InlineData("Sansha Incursion HQ System Effects")]   // incursion — no curated entry yet
    [InlineData("SOEEB12 (DO NOT TRANSLATE)")]            // event beacon junk
    [InlineData("Amo - Tribal Liberation Force War HQ")]  // faction-war HQ
    [InlineData("System-Wide Warp Speed Bonus")]          // misc system bonus
    public void Classify_NonEnvironmentBeacons_AreRejected(string name)
    {
        Assert.Null(EnvironmentBeaconClassifier.Classify(1, name));
    }

    [Fact]
    public void Classify_OrdersWormholeBeforeMetaliminalBeforeTriglavian_TierAscending()
    {
        var pulsar1 = EnvironmentBeaconClassifier.Classify(30844, "Class 1 Pulsar Effects")!;
        var pulsar6 = EnvironmentBeaconClassifier.Classify(30869, "Class 6 Pulsar Effects")!;
        var storm = EnvironmentBeaconClassifier.Classify(56057, "Strong Metaliminal Electrical Storm")!;
        var trig = EnvironmentBeaconClassifier.Classify(52249, "Triglavian Invasion Strong System Effects")!;

        Assert.True(pulsar1.SortOrder < pulsar6.SortOrder);
        Assert.True(pulsar6.SortOrder < storm.SortOrder);
        Assert.True(storm.SortOrder < trig.SortOrder);
    }

    [Fact]
    public void Selector_NoneIsFirstAndDefault_SelectingABeaconRaisesChangeAndYieldsInput()
    {
        var sde = new FakeSdeAccessor()
            .Add(30869, "Class 6 Pulsar Effects", 920, 2)   // intentionally out of order to prove sorting
            .Add(30844, "Class 1 Pulsar Effects", 920, 2);
        var selector = new WeatherSelectorViewModel(sde);

        Assert.Equal(["None", "Pulsar C1", "Pulsar C6"], selector.Options.Select(option => option.Label));
        Assert.Equal("None", selector.SelectedOption.Label);
        Assert.Null(selector.CurrentWeather);

        var raised = 0;
        selector.WeatherChanged += (_, _) => raised++;
        selector.SelectedOption = selector.Options.Single(option => option.TypeId == 30844);

        Assert.Equal(1, raised);
        Assert.Equal(30844, selector.CurrentWeather?.TypeId);
    }

    [Fact]
    public void Selector_NoSde_OffersOnlyNone()
    {
        var selector = new WeatherSelectorViewModel(sde: null);
        Assert.Equal("None", Assert.Single(selector.Options).Label);
        Assert.Null(selector.CurrentWeather);
    }
}
