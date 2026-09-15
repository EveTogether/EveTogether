using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;
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
        // A normal mining fleet (no known site capacity): the total, never the site progress bar.
        Assert.False(_Mining(fleet.Jithran)!.ShowSiteRemaining);
    }

    [AvaloniaFact]
    public async Task OnAMetaliminalHomefront_RemainingSubtractsTheFleetsMinedAndResidue()
    {
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync(MetaliminalSite.DungeonId);

        await fleet.MineAsync(fleet.Jithran, "Amperum Mutanite", 3000, residueUnits: 500);
        await fleet.MineAsync(fleet.Raymond, "Amperum Mutanite", 1000);

        await fleet.SettleAsync(() => _Mining(fleet.Jithran)?.ShowSiteRemaining == true);

        // 5,000 − (3,000 + 1,000) − 500 residue = 500 (no crit here, so unaffected by ET-299's fix).
        Assert.Equal("500 / 5,000 units left", _Mining(fleet.Jithran)!.SiteRemainingLabel);
        Assert.Equal("500 / 5,000 units left", _Mining(fleet.Raymond)!.SiteRemainingLabel);
    }

    /// <summary>ET-299: crit is bonus yield to the hold and costs the asteroid nothing — the old code subtracted
    /// crit-inclusive units from capacity, double-counting the crit as depletion. The real depletion is
    /// basis units (units − crit) plus residue, own rows and shared fleet members alike; a shared member's total-only
    /// share (no per-ore crit split) is assumed to carry no crit.</summary>
    [AvaloniaFact]
    public async Task OnAMetaliminalHomefront_CritDoesNotDoubleCountAsDepletion()
    {
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync(MetaliminalSite.DungeonId);

        // Jithran's own row: 3,000 units of which 500 are crit, plus 500 residue.
        // Depletion so far: (3,000 − 500) + 500 = 3,000, so 2,000 should remain — not 5,000 − 3,000 − 500 = 1,500.
        await fleet.MineAsync(fleet.Jithran, "Amperum Mutanite", 3000, criticalUnits: 500, residueUnits: 500);

        await fleet.SettleAsync(() => _Mining(fleet.Jithran)?.ShowSiteRemaining == true);

        Assert.Equal("2,000 / 5,000 units left", _Mining(fleet.Jithran)!.SiteRemainingLabel);
        Assert.Equal(0.4, _Mining(fleet.Jithran)!.SiteRemainingFraction, precision: 5);
    }

    [AvaloniaFact]
    public async Task SwitchingMiningOff_TakesItOffTheOthersTotal_AtOnce()
    {
        // A homefront kind claims MINING outright regardless of who has mined yet (RunTypeCatalogue), unlike a plain
        // site, which only shows it live once its own participants have — Raymond mines nothing here, so his own
        // window needs the outright claim to have a MINING section to read the fleet total from at all.
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync(MetaliminalSite.DungeonId);
        await fleet.MineAsync(fleet.Jithran, "Amperum Mutanite", 1000);
        await fleet.SettleAsync(() => _Mining(fleet.Raymond)?.FleetMinedText?.Contains("1,000") == true);

        await fleet.Jithran.Window.FleetSharing.ToggleMiningCommand.ExecuteAsync(null);
        await fleet.SettleAsync(() => _Mining(fleet.Raymond)?.FleetMinedText?.Contains("1,000") != true);

        Assert.False(fleet.Jithran.Window.FleetSharing.IsSharingMining);
        Assert.DoesNotContain("1,000", _Mining(fleet.Raymond)!.FleetMinedText ?? "");
    }

    /// <summary>ET-283: RunShareUpdate now carries one line per ore (mirroring loot's RunShareLootLine[]), so a
    /// receiver draws the same per-character/per-ore group for a fleet mate on another PC as for its own rows.</summary>
    [AvaloniaFact]
    public async Task TwoPilotsMiningDifferentOres_EachGetsAPerOreGroupRowForTheOther()
    {
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync();
        await fleet.MineAsync(fleet.Jithran, "Veldspar II-Grade", 1000, residueUnits: 50);
        await fleet.MineAsync(fleet.Raymond, "Scordite", 500);

        await fleet.SettleAsync(() =>
            _Mining(fleet.Jithran)?.Groups.Count == 2 && _Mining(fleet.Raymond)?.Groups.Count == 2);

        MiningCharacterGroupViewModel? raymondOnJithran =
            _Mining(fleet.Jithran)!.Groups.FirstOrDefault(group => group.CharacterId == FleetOfTwo.RaymondId);
        Assert.NotNull(raymondOnJithran);
        Assert.False(raymondOnJithran!.IsFallbackTotalOnly);
        Assert.Contains(raymondOnJithran.Ores, ore => ore.OreText == "Scordite" && ore.Units == 500);

        MiningCharacterGroupViewModel? jithranOnRaymond =
            _Mining(fleet.Raymond)!.Groups.FirstOrDefault(group => group.CharacterId == FleetOfTwo.JithranId);
        Assert.NotNull(jithranOnRaymond);
        Assert.Contains(jithranOnRaymond!.Ores, ore => ore.OreText == "Veldspar II-Grade" && ore.Units == 1000
                                                        && ore.ResidueUnits == 50);

        // Each pilot's own group is local; the other is a shared fleet mate.
        Assert.True(_Mining(fleet.Jithran)!.Groups.First(group => group.CharacterId == FleetOfTwo.JithranId).IsLocal);
        Assert.False(raymondOnJithran.IsLocal);
    }

    /// <summary>An older client that never learned per-ore lines still shares its total (ET-234's shape); the
    /// receiver draws it as the "all ores" fallback row rather than crashing or hiding the member entirely (ET-283).</summary>
    [AvaloniaFact]
    public async Task AnOlderClientsShare_WithoutPerOreLines_DrawsTheFallbackRow()
    {
        // A homefront kind claims MINING outright regardless of who has mined yet (RunTypeCatalogue) — Jithran never
        // mines here, only Raymond's share arrives, so the outright claim is what gives his own window a MINING
        // section to draw the fallback row on at all (the same reason the other homefront tests above use it).
        using FleetOfTwo fleet = await FleetOfTwo.CreateAsync(MetaliminalSite.DungeonId);
        await fleet.SettleAsync(() => fleet.Jithran.Window.FleetSharing.IsShown && fleet.Raymond.Window.FleetSharing.IsShown);

        RunShareUpdate oldStyleShare = new(FleetOfTwo.FleetId, FleetOfTwo.GroupCode,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), SharesLoot: false, SharesBounty: false, CaptureCount: 0,
            Loot: [], SharesMining: true, MinedUnits: 1500, ResidueUnits: 200);
        await fleet.Raymond.Instance.Services.GetRequiredService<IEventBus>()
            .PublishAsync(new FleetRunShareEvent(oldStyleShare, FleetOfTwo.RaymondId), EventTarget.Remote);
        await fleet.SettleAsync(() => _Mining(fleet.Jithran)?.Groups.Any(group => group.IsFallbackTotalOnly) == true);

        MiningCharacterGroupViewModel raymondGroup =
            _Mining(fleet.Jithran)!.Groups.First(group => group.CharacterId == FleetOfTwo.RaymondId);
        Assert.True(raymondGroup.IsFallbackTotalOnly);
        Assert.False(raymondGroup.HasOres);
        Assert.Contains("1,500 units", raymondGroup.FallbackText);
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
