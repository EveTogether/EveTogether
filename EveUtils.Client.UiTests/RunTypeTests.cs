using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Enums;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-226: the run type catalogue — one source (<see cref="RunTypeResolver"/>) turning what a run already carries
/// into a <see cref="RunTypeId"/>, one place (<see cref="RunTypeCatalogue"/>) turning that into a name and icon.
/// </summary>
public sealed class RunTypeTests
{
    // ── RunTypeResolver: the source, never a name guessed from the site ────────────────────────────────

    [Theory]
    [InlineData("Combat Site", RunTypeId.CombatSite)]
    [InlineData("Data Site", RunTypeId.DataSite)]
    [InlineData("Relic Site", RunTypeId.RelicSite)]
    [InlineData("Gas Site", RunTypeId.GasSite)]
    [InlineData("Ore Site", RunTypeId.OreSite)]
    [InlineData("Wormhole", RunTypeId.Wormhole)]
    public void Resolve_ForASiteWithARecognisedScannerGroup_ReturnsTheMatchingType(string group, RunTypeId expected) =>
        Assert.Equal(expected, RunTypeResolver.Resolve(ActivityKind.Site, group));

    /// <summary>Jithran's original report (ET-226): a Data Site must not resolve to Combat Site, or to anything but
    /// itself — this is the one arm every catalogue entry stands or falls on.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Cosmic Signature")]
    [InlineData("Combat Site (Sansha's Nation)")]
    public void Resolve_ForASiteWithNoRecognisedScannerGroup_ReturnsUnknown_NeverCombatSite(string? group) =>
        Assert.Equal(RunTypeId.Unknown, RunTypeResolver.Resolve(ActivityKind.Site, group));

    /// <summary>The widened scope of ET-226 (Jithran, 2026-09-11): a mission's TYPE follows <c>ActivityKind</c>
    /// alone and must never depend on a scanner group a mission never has.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Combat Site")]
    public void Resolve_ForAMission_AlwaysReturnsMission_RegardlessOfSignatureGroup(string? group) =>
        Assert.Equal(RunTypeId.Mission, RunTypeResolver.Resolve(ActivityKind.Mission, group));

    [Fact]
    public void Resolve_ForAnAbyssal_ReturnsAbyssal() =>
        Assert.Equal(RunTypeId.Abyssal, RunTypeResolver.Resolve(ActivityKind.Abyssal, null));

    /// <summary>ET-228: the dungeon id is the more specific fact, read ahead of the scanner group text — a homefront
    /// resolves to its own type even though its scanner group (when caught at all) reads like an ordinary site.
    /// 10347 is Raid: Hall of Sacrifice (domain/homefronts.md §2).</summary>
    [Fact]
    public void Resolve_ForASiteWithAHomefrontDungeonId_ReturnsHomefront_EvenWithACombatSiteGroup() =>
        Assert.Equal(RunTypeId.Homefront, RunTypeResolver.Resolve(ActivityKind.Site, "Combat Site", siteTypeId: 10347));

    /// <summary>0 is never a homefront id — the sentinel every unmatched site already carries — so an ordinary
    /// Combat Site with no catalogue match still resolves by its scanner group, unaffected by ET-228's new arm.</summary>
    [Fact]
    public void Resolve_ForASiteWithNoHomefrontDungeonId_FallsBackToTheScannerGroup() =>
        Assert.Equal(RunTypeId.CombatSite, RunTypeResolver.Resolve(ActivityKind.Site, "Combat Site", siteTypeId: 0));

    // ── RunTypeCatalogue: every declared type has a name and an icon ───────────────────────────────────

    /// <summary>
    /// Counter-proof: a <see cref="RunTypeId"/> appended without its row in <c>RunTypeCatalogue.Definitions</c>
    /// does not throw — <see cref="RunTypeCatalogue.For"/> falls back to Unknown's row on purpose (AGENTS.md §2) —
    /// so a missing entry would otherwise pass silently. Asserting <c>definition.Id == id</c> catches exactly that:
    /// commenting out the <see cref="RunTypeId.DataSite"/> row in <c>RunTypeCatalogue</c> turns this red (the
    /// lookup falls back to Unknown, whose <c>Id</c> disagrees with the one asked for) — checked by hand against
    /// this fix.
    /// </summary>
    [Theory]
    [InlineData(RunTypeId.Unknown)]
    [InlineData(RunTypeId.CombatSite)]
    [InlineData(RunTypeId.DataSite)]
    [InlineData(RunTypeId.RelicSite)]
    [InlineData(RunTypeId.GasSite)]
    [InlineData(RunTypeId.OreSite)]
    [InlineData(RunTypeId.Wormhole)]
    [InlineData(RunTypeId.Mission)]
    [InlineData(RunTypeId.Mining)]
    [InlineData(RunTypeId.Homefront)]
    [InlineData(RunTypeId.Abyssal)]
    public void For_EveryDeclaredType_HasItsOwnNameAndIcon(RunTypeId id)
    {
        RunTypeDefinition definition = RunTypeCatalogue.For(id);

        Assert.Equal(id, definition.Id);
        Assert.False(string.IsNullOrWhiteSpace(definition.Name));
    }

    /// <summary>Eleven different silhouettes, not eleven shades of one (ET-226 AC-4: legible with no colour at
    /// all) — two types sharing an icon would make TYPE unreadable in a screenshot or for a colour-blind reader.
    /// </summary>
    [Fact]
    public void For_NoTwoTypesShareAnIcon()
    {
        MaterialIconKind[] icons =
        [
            RunTypeCatalogue.For(RunTypeId.Unknown).Icon,
            RunTypeCatalogue.For(RunTypeId.CombatSite).Icon,
            RunTypeCatalogue.For(RunTypeId.DataSite).Icon,
            RunTypeCatalogue.For(RunTypeId.RelicSite).Icon,
            RunTypeCatalogue.For(RunTypeId.GasSite).Icon,
            RunTypeCatalogue.For(RunTypeId.OreSite).Icon,
            RunTypeCatalogue.For(RunTypeId.Wormhole).Icon,
            RunTypeCatalogue.For(RunTypeId.Mission).Icon,
            RunTypeCatalogue.For(RunTypeId.Mining).Icon,
            RunTypeCatalogue.For(RunTypeId.Homefront).Icon,
            RunTypeCatalogue.For(RunTypeId.Abyssal).Icon
        ];

        Assert.Equal(icons.Length, icons.ToHashSet().Count);
    }

    // ── ET-228: the per-run refinement ET-236's design left for a homefront's own kind ─────────────────

    /// <summary>Archetype 70 gives "Homefront" plus the kind (ET-228 AC-2); the kind is read off the dungeon id, not
    /// stored on the run. A combat homefront keeps the base row's ENEMIES/BOUNTY and claims no MINING it never
    /// needed.</summary>
    [Fact]
    public void For_ForACombatHomefront_NamesItsKind_AndClaimsNoMining()
    {
        RunTypeDefinition raid = RunTypeCatalogue.For(ActivityKind.Site, "Combat Site", siteTypeId: 10347);

        Assert.Equal("Homefront · Raid", raid.Name);
        Assert.DoesNotContain(RunSectionId.Mining, raid.WindowSections);
        Assert.DoesNotContain(RunSectionId.Mining, raid.DetailSections);
        Assert.Contains(RunSectionId.Enemies, raid.WindowSections);
        Assert.Contains(RunSectionId.Bounty, raid.WindowSections);
    }

    /// <summary>Metaliminal Meteoroid and Abyssal Artifact Recovery are mining homefronts (domain/homefronts.md §2)
    /// — their catalogue row claims MINING outright, the way a combat homefront already claims ENEMIES/BOUNTY,
    /// rather than waiting for the reactive ET-162 rule to notice mining after the fact.</summary>
    [Theory]
    [InlineData(10312, "Metaliminal Meteoroid")]
    [InlineData(10346, "Abyssal Artifact Recovery")]
    public void For_ForAMiningHomefront_NamesItsKind_AndClaimsMining(int dungeonId, string kind)
    {
        RunTypeDefinition site = RunTypeCatalogue.For(ActivityKind.Site, null, siteTypeId: dungeonId);

        Assert.Equal($"Homefront · {kind}", site.Name);
        Assert.Contains(RunSectionId.Mining, site.WindowSections);
        Assert.Contains(RunSectionId.Mining, site.DetailSections);
    }

    /// <summary>ET-234: a Metaliminal Meteoroid's single 5,000-unit asteroid is the one homefront capacity MINING's
    /// "remaining" line can subtract against. AAR's 9 waves of 12 asteroids is not a comparably simple figure, so it
    /// carries none — the fleet still sees a total, just no "remaining".</summary>
    [Theory]
    [InlineData(10312, 5000)]
    [InlineData(10346, null)]
    public void For_ForAMiningHomefront_CarriesSiteMiningCapacity_OnlyForMetaliminal(int dungeonId, int? expectedCapacity)
    {
        RunTypeDefinition site = RunTypeCatalogue.For(ActivityKind.Site, null, siteTypeId: dungeonId);

        Assert.Equal(expectedCapacity, site.SiteMiningCapacityUnits);
    }

    // ── The run window's own TYPE text (ET-226 widened scope) ──────────────────────────────────────────

    /// <summary>
    /// Counter-proof: the screenshot Jithran attached to the widened ticket (2026-09-11) — a mission run, TYPE
    /// "not known yet" — even though <c>Kind</c> already settled on <c>ActivityKind.Mission</c> the moment the
    /// window opened. Red against the pre-fix code (<c>ActivityWindowViewModel.cs:942</c>,
    /// <c>SignatureGroup ?? "not known yet"</c>), which only ever read the (always-null, for a mission) scanner
    /// group and never the kind.
    /// </summary>
    [Fact]
    public void SignatureTypeText_ForAMissionWithNoSignatureGroup_ReadsMissionRun_NeverNotKnownYet()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Mission, _Unused());

        Assert.Equal("Mission run", model.Activity().SignatureTypeText);
        Assert.Equal(MaterialIconKind.ClipboardTextOutline, model.Activity().TypeIcon);
    }

    [Fact]
    public void SignatureTypeText_ForASiteWithARecognisedGroup_ReadsTheSpecificType()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Site, _Unused()) { SignatureGroup = "Relic Site" };

        Assert.Equal("Relic Site", model.Activity().SignatureTypeText);
        Assert.Equal(MaterialIconKind.DiamondStone, model.Activity().TypeIcon);
    }

    /// <summary>A manual start, or a site the scanner never named a group for, reads "Site" — never "not known
    /// yet" (dropped alongside the rest of the raw <c>SignatureGroup</c> text) and never a specific kind it does
    /// not know (ET-226 AC-3).</summary>
    [Fact]
    public void SignatureTypeText_ForASiteWithNoSignatureGroup_ReadsSite()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Site, _Unused());

        Assert.Equal("Site", model.Activity().SignatureTypeText);
    }

    private static IServiceProvider _Unused() => new ServiceCollection().BuildServiceProvider();

    // ── ET-255: every catalogue type is manually startable, or says why not ───────────────────────────

    /// <summary>A row with no reason recorded below and no <see cref="ManualStartRequirement"/> of its own is a type
    /// Tools → Start run silently cannot offer, with nobody having decided that on purpose — the guard ET-255 asked
    /// for. Counter-proof: give <c>RunTypeId.Wormhole</c> a <c>ManualStart</c> without removing its entry below, or
    /// take <c>RunTypeId.Homefront</c>'s entry out without giving the row a <c>ManualStart</c>, and this goes red
    /// either way.</summary>
    [Fact]
    public void EveryCatalogueType_IsManuallyStartable_OrSaysWhyNot()
    {
        foreach ((RunTypeId id, RunTypeDefinition row) in RunTypeCatalogue.Rows)
        {
            bool recordedHere = NotManuallyStartable.ContainsKey(id);
            Assert.True(row.ManualStart is not null || recordedHere,
                $"{row.Name} has no ManualStart and no reason recorded in NotManuallyStartable below");
            Assert.False(row.ManualStart is not null && recordedHere,
                $"{row.Name} has a ManualStart now — drop it from NotManuallyStartable below");
        }
    }

    /// <summary>Why each of these rows declares no <see cref="RunTypeDefinition.ManualStart"/> — the same shape
    /// <c>RunSectionFrameworkTests.KeptOnPurpose</c> uses for the kind checks it allows. Not Mining: that row
    /// declares one (<see cref="ManualStartRequirement.None"/>) and, since ET-265 gave it its own
    /// <see cref="ActivityKind"/>, is reachable through <c>ManualRunStartViewModel</c> too.</summary>
    private static readonly IReadOnlyDictionary<RunTypeId, string> NotManuallyStartable = new Dictionary<RunTypeId, string>
    {
        [RunTypeId.CombatSite] = "resolved only from a copied signature's scanner group, never from this dialog's own choice",
        [RunTypeId.DataSite] = "resolved only from a copied signature's scanner group, never from this dialog's own choice",
        [RunTypeId.RelicSite] = "resolved only from a copied signature's scanner group, never from this dialog's own choice",
        [RunTypeId.GasSite] = "resolved only from a copied signature's scanner group, never from this dialog's own choice",
        [RunTypeId.OreSite] = "resolved only from a copied signature's scanner group, never from this dialog's own choice",
        [RunTypeId.Wormhole] = "resolved only from a copied signature's scanner group, never from this dialog's own choice",
        [RunTypeId.Homefront] = "resolved only from a matched dungeon id (ET-228), never chosen directly — a "
            + "homefront picked by name through Tools → Start run's own \"Site\" row (ManualStartRequirement.Site "
            + "on RunTypeId.Unknown) already carries its dungeon id and resolves here on its own"
    };
}
