using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-234: mining follows loot and bounty's own opt-in rules on a shared fleet run (ET-242) — a member's mined units
/// travel on <see cref="RunShareUpdate"/> alongside the loot fields, so the run window's MINING section can show a
/// "fleet mined" line over everyone who shares, and — on a Metaliminal Meteoroid homefront, whose 5,000-unit capacity
/// is known — a "remaining" line.
/// </summary>
public sealed class FleetRunMiningSharingTests
{
    // The dungeon id alone resolves TYPE and its 5,000-unit capacity (RunTypeCatalogue.For, ET-228/ET-234) — no
    // scanner group needed, the same single-match rule ActivityWindowViewModel itself uses.
    private static readonly SdeSite MetaliminalSite = new(10312, "Metaliminal Meteoroid: Amarr Mining", ArchetypeId: 70,
        ArchetypeName: null, FactionId: null, FactionName: null, Description: null, DedRating: null,
        IsShipRestricted: false, AllowedShipGroups: []);

    [AvaloniaFact]
    public async Task TwoPilotsMining_EachSeesTheirOwnPlusTheFleetTotal()
    {
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync();
        await fleet.MineAsync(fleet.Jithran, "Amperum Mutanite", 1000);
        await fleet.MineAsync(fleet.Raymond, "Amperum Mutanite", 500);

        await fleet.SettleAsync(() => _Mining(fleet.Jithran)?.FleetMinedText?.Contains("1,500") == true);

        Assert.True(fleet.Jithran.Window.FleetSharing.IsSharingMining);
        Assert.Contains("1,500 units", _Mining(fleet.Jithran)!.FleetMinedText);
        Assert.Contains("1,500 units", _Mining(fleet.Raymond)!.FleetMinedText);
        // A normal mining fleet (no known site capacity): the total, never "remaining".
        Assert.Null(_Mining(fleet.Jithran)!.RemainingText);
    }

    [AvaloniaFact]
    public async Task OnAMetaliminalHomefront_RemainingSubtractsTheFleetsMinedAndResidue()
    {
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync();
        fleet.Jithran.Window.MatchedSites = [MetaliminalSite];
        fleet.Raymond.Window.MatchedSites = [MetaliminalSite];

        await fleet.MineAsync(fleet.Jithran, "Amperum Mutanite", 3000, residueUnits: 500);
        await fleet.MineAsync(fleet.Raymond, "Amperum Mutanite", 1000);

        await fleet.SettleAsync(() => _Mining(fleet.Jithran)?.RemainingText is not null);

        // 5,000 − (3,000 + 1,000) − 500 residue = 500.
        Assert.Contains("500 units remaining", _Mining(fleet.Jithran)!.RemainingText);
        Assert.Contains("500 units remaining", _Mining(fleet.Raymond)!.RemainingText);
    }

    [AvaloniaFact]
    public async Task SwitchingMiningOff_TakesItOffTheOthersTotal_AtOnce()
    {
        // A homefront kind claims MINING outright regardless of who has mined yet (RunTypeCatalogue), unlike a plain
        // site, which only shows it live once its own participants have — Raymond mines nothing here, so his own
        // window needs the outright claim to have a MINING section to read the fleet total from at all.
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync();
        fleet.Jithran.Window.MatchedSites = [MetaliminalSite];
        fleet.Raymond.Window.MatchedSites = [MetaliminalSite];
        await fleet.MineAsync(fleet.Jithran, "Amperum Mutanite", 1000);
        await fleet.SettleAsync(() => _Mining(fleet.Raymond)?.FleetMinedText?.Contains("1,000") == true);

        await fleet.Jithran.Window.FleetSharing.ToggleMiningCommand.ExecuteAsync(null);
        await fleet.SettleAsync(() => _Mining(fleet.Raymond)?.FleetMinedText?.Contains("1,000") != true);

        Assert.False(fleet.Jithran.Window.FleetSharing.IsSharingMining);
        Assert.DoesNotContain("1,000", _Mining(fleet.Raymond)!.FleetMinedText ?? "");
    }

    [Fact]
    public void MetricKind_MiningYield_IsOptInAndRunScoped_LikeLootAndBounty()
    {
        Assert.True(MetricShareSnapshot.IsOptIn(MetricKind.MiningYield));
        Assert.True(MetricShareSnapshot.IsRunScoped(MetricKind.MiningYield));
    }

    /// <summary>Off a shared run the opt-in stands exactly as loot and bounty's does: private until turned on, globally
    /// or for the fleet; on one, the run's own choice first, then the fleet's.</summary>
    [Theory]
    [InlineData(false, null, null, null, false)]
    [InlineData(true, null, null, null, true)]
    [InlineData(true, null, "false", null, false)]
    [InlineData(true, null, "false", "true", true)]
    public void WhoSeesMining_FollowsTheRunThenTheFleetThenTheGlobalChoice(
        bool onSharedRun, string? global, string? fleetOverride, string? runChoice, bool expected)
    {
        const long fleetId = FleetOfTwo.FleetId;
        const int characterId = FleetOfTwo.JithranId;
        const string groupCode = FleetOfTwo.GroupCode;
        Dictionary<string, string> values = [];
        if (global is not null)
            values[MetricShareSnapshot.KeyFor(MetricKind.MiningYield)] = global;
        if (fleetOverride is not null)
            values[MetricShareSnapshot.OverrideKeyFor(fleetId, characterId, MetricKind.MiningYield)] = fleetOverride;
        if (runChoice is not null)
            values[MetricShareSnapshot.RunKeyFor(groupCode, MetricKind.MiningYield)] = runChoice;

        MetricShareSnapshot snapshot = new(values,
            onSharedRun ? new Dictionary<(long FleetId, int CharacterId), string> { [(fleetId, characterId)] = groupCode } : null);

        Assert.Equal(expected, snapshot.IsShared(fleetId, characterId, MetricKind.MiningYield));
    }

    [Fact]
    public void TheFleetsSharingDialog_OffersMining_AsItsOwnChoice()
    {
        FleetShareViewModel dialog = new("Tuesday op", FleetOfTwo.FleetId, [(FleetOfTwo.JithranId, "Jithran")],
            new MetricShareSnapshot(new Dictionary<string, string>()));
        dialog.AllCharacters.Metrics.Single(row => row.Kind == MetricKind.MiningYield).ChoiceIndex = 2;

        Assert.Contains(dialog.BuildOverrides(), write =>
            write.Key == MetricShareSnapshot.OverrideKeyFor(FleetOfTwo.FleetId, FleetOfTwo.JithranId, MetricKind.MiningYield)
            && write.Value == "false");
    }

    private static MiningWindowSectionViewModel? _Mining(Pilot pilot)
    {
        pilot.Refresh();
        return pilot.Window.Sections.OfType<MiningWindowSectionViewModel>().FirstOrDefault();
    }
}
