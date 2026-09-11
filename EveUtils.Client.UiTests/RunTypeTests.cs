using System;
using System.Linq;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
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

        Assert.Equal("Mission run", model.SignatureTypeText);
        Assert.Equal(MaterialIconKind.ClipboardTextOutline, model.TypeIcon);
    }

    [Fact]
    public void SignatureTypeText_ForASiteWithARecognisedGroup_ReadsTheSpecificType()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Site, _Unused()) { SignatureGroup = "Relic Site" };

        Assert.Equal("Relic Site", model.SignatureTypeText);
        Assert.Equal(MaterialIconKind.DiamondStone, model.TypeIcon);
    }

    /// <summary>A manual start, or a site the scanner never named a group for, reads "Site" — never "not known
    /// yet" (dropped alongside the rest of the raw <c>SignatureGroup</c> text) and never a specific kind it does
    /// not know (ET-226 AC-3).</summary>
    [Fact]
    public void SignatureTypeText_ForASiteWithNoSignatureGroup_ReadsSite()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Site, _Unused());

        Assert.Equal("Site", model.SignatureTypeText);
    }

    private static IServiceProvider _Unused() => new ServiceCollection().BuildServiceProvider();
}
