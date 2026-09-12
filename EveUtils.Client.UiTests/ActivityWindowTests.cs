using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Clipboard;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.Theming;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Client.Views;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using EveUtils.Shared.Modules.Settings.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The activity window's frame (ET-98 phase 1): that every section answers for itself with its body shut, that the
/// manual weather/tier survives the window being closed and reopened, that no label ever implies the window valued
/// the loot itself, and that all four faction palettes actually reach it — which on an <see cref="OverlayWindow"/>
/// rather than a <c>ChromedWindow</c> is worked for rather than inherited.
/// </summary>
public class ActivityWindowTests
{
    private static readonly DateTime Anchor = new(2026, 9, 1, 20, 0, 0, DateTimeKind.Utc);


    // ── AC-1 — every section says something, open or shut ───────────────────────────────────────────

    [Theory]
    // An abyssal pocket has no NPC bounty at all (ET-241) but does spend a filament CONSUMABLES shows (ET-249) —
    // still six sections, one traded for the other rather than simply one fewer than a site's own six.
    [InlineData(ActivityKind.Abyssal, 6)]
    [InlineData(ActivityKind.Site, 6)]
    public void EmptyRun_EverySectionSummary_SaysSomething(ActivityKind kind, int expectedSectionCount)
    {
        var model = new ActivityWindowViewModel(kind, _Unused());

        Assert.Equal(expectedSectionCount, model.Sections.Count);
        foreach (var section in model.Sections)
            Assert.False(string.IsNullOrWhiteSpace(section.HeaderSummary),
                $"{section.Title} is silent with its body shut on an empty {kind} run");
    }

    [Theory]
    [InlineData(ActivityKind.Abyssal)]
    [InlineData(ActivityKind.Site)]
    public void FilledRun_EverySectionSummary_SaysSomething(ActivityKind kind)
    {
        var model = _Filled(kind);
        model.WeatherIndex = 3;
        model.TierIndex = 4;
        model.Refresh(Anchor.AddMinutes(6));

        foreach (var section in model.Sections)
            Assert.False(string.IsNullOrWhiteSpace(section.HeaderSummary),
                $"{section.Title} is silent with its body shut on a filled {kind} run");
    }

    [Fact]
    public void ASectionWithoutItsOwnDependency_DoesNotWaitOnAnotherTicket()
    {
        var abyssal = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());

        Assert.DoesNotContain("ET-40", abyssal.Fit().HeaderSummary);
        Assert.Contains("no fit", abyssal.Fit().HeaderSummary);
        Assert.Equal("no loot captured", abyssal.Loot().HeaderSummary);
        // ACTIVITY no longer waits on anything (ET-80): with nothing copied it names the gap instead of a ticket.
        Assert.Equal("no signature", new ActivityWindowViewModel(ActivityKind.Site, _Unused()).Activity().HeaderSummary);
    }

    /// <summary>ET-107 AC-3. The four detection states of ET-101 have to survive the automatic fill: in all four the
    /// run begins without a fit, and the four reasons are four different remedies. Compared pairwise rather than only
    /// against a keyword, so folding any two into one shared line fails here.</summary>
    [Fact]
    public void FitDetection_TheFourEmptyStatesStayFourAndEachSaysWhyItIsEmpty()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());

        var texts = new List<string>();
        foreach (var reading in new[]
        {
            ShipFitDetectionReading.Unobserved,
            ShipFitDetectionReading.ScopeMissing,
            _Observed(null, ShipFitMatchReason.NoFitFound),
            _Observed(null, ShipFitMatchReason.AmbiguousShipType),
        })
        {
            model.Fit().ApplyFitDetection(reading);
            Assert.False(model.Fit().HasFit, "the run must begin without a fit when there is nothing to fill it with");
            texts.Add(model.Fit().FitText);
        }

        Assert.Equal(4, texts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // The two that collapse most easily, and that mean opposite things: may this character look at all, or did it
        // look and find nothing.
        Assert.NotEqual(texts[1], texts[2]);
        Assert.Contains("scope", texts[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no known fit", texts[2], StringComparison.OrdinalIgnoreCase);
        Assert.All(texts, text => Assert.StartsWith("no fit:", text, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(model.Fit().ChooseFitCommand);
    }

    /// <summary>ET-107 AC-1. Starting a run fills the fit from ET-101's reading — the player confirms nothing. Driven
    /// through <see cref="ActivityWindowViewModel.Refresh"/>, which is what START itself calls.</summary>
    [Fact]
    public void StartingARun_FillsTheFitFromTheDetectionWithoutBeingConfirmed()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal,
            _WithFitDetection(_Observed(new ShipFitCandidate(7, "Escalation 3/10 - LZ", 17715), ShipFitMatchReason.ShipName)));

        model.StartManualRun(Anchor);

        Assert.True(model.Fit().HasFit);
        Assert.Contains("Escalation 3/10 - LZ", model.Fit().FitText);
        Assert.Contains("name matches the observed ship", model.Fit().FitText);
        Assert.Equal(model.Fit().FitText, model.Fit().HeaderSummary);
    }

    /// <summary>ET-107 AC-2 and AC-4. One fit per run with its origin as a detail: a manual choice arrives through the
    /// same reading as any other, so there is no proposal for it to stand beside and nothing to confirm.</summary>
    [Fact]
    public void AManualChoice_IsTheRunsFitRatherThanASecondFieldBesideAProposal()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal,
            _WithFitDetection(_Observed(new ShipFitCandidate(8, "Hand-picked Gila", 17715), ShipFitMatchReason.Manual)));

        model.Refresh(Anchor);

        Assert.True(model.Fit().HasFit);
        Assert.StartsWith("fit: Hand-picked Gila", model.Fit().FitText);
        Assert.Contains("manual choice", model.Fit().FitText);
        Assert.DoesNotContain("suggest", model.Fit().FitText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(typeof(ActivityWindowViewModel).GetProperties(),
            property => property.Name.Contains("Suggest", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Chosen", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>ET-107 AC-2, the other correction. Unlinking has to hold — against the next tick, and against the
    /// window being closed and reopened on the same run, which builds a fresh view model that adopts the open row.
    /// A deliberate act that a reopen quietly undoes is the same fault as a run that forgets its enemies at STOP.</summary>
    [Fact]
    public async Task DetachingTheFit_SurvivesTheNextTickAndTheWindowBeingReopenedOnTheSameRun()
    {
        IServiceProvider services =
            _WithFitDetection(_Observed(new ShipFitCandidate(7, "Escalation 3/10 - LZ", 17715), ShipFitMatchReason.ShipName));
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, services);
        model.StartManualRun(Anchor);
        Assert.True(model.Fit().HasFit);

        await model.Fit().DetachFitCommand.ExecuteAsync(null);
        model.Refresh(Anchor.AddSeconds(1));
        await model.Fit().RefreshFitAsync();

        Assert.False(model.Fit().HasFit);
        Assert.Contains("unlinked", model.Fit().FitText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no max velocity", model.Fit().FitVelocityText);

        // Closing and reopening mid-run: a new view model over the same services, as DialogService builds one.
        model.Dispose();
        var reopened = new ActivityWindowViewModel(ActivityKind.Abyssal, services);
        reopened.StartManualRun(Anchor.AddSeconds(2));

        Assert.False(reopened.Fit().HasFit);
        Assert.Contains("unlinked", reopened.Fit().FitText, StringComparison.OrdinalIgnoreCase);
    }

    private static ShipFitDetectionReading _Observed(ShipFitCandidate? selected, ShipFitMatchReason reason) =>
        new(ShipFitDetectionState.Observed, Anchor, 17715, 9, "Gila", selected, reason,
            selected is null ? [] : [selected]);

    /// <summary>
    /// A window that knows whose run it is without asking, before START — the character this client publishes fleet
    /// metrics as, which is the same set the FLEET section beside it is drawn from.
    ///
    /// It used to be an <c>IActiveFleetState</c> stub, and that stub was the reason these tests passed while the
    /// window did not: nothing but the fleets window's own row selection ever fills that state, so on Raymond's
    /// client it was empty while the FLEET section listed two members, and the FIT section printed a question it had
    /// no one left to ask. <see cref="IActiveFleetState"/> is deliberately not registered here — the window must not
    /// go looking for it again.
    /// </summary>
    private static IServiceProvider _WithFitDetection(ShipFitDetectionReading reading)
    {
        var participation = new FleetParticipation();
        participation.Set([new FleetParticipant(1, 900, ClientOnly: true)]);
        return new ServiceCollection()
            .AddSingleton<IShipFitDetectionService>(new FakeShipFitDetection(reading))
            .AddSingleton<IFleetParticipation>(participation)
            .BuildServiceProvider();
    }

    /// <summary>Stands in for the service's own store: detaching changes what every later reading says, including one
    /// taken by a window opened after the first was closed.</summary>
    private sealed class FakeShipFitDetection(ShipFitDetectionReading reading) : IShipFitDetectionService
    {
        private ShipFitDetectionReading _reading = reading;

        public ShipFitDetectionReading GetReading(int characterId) => _reading;

        public Task<Result> SetManualFitAsync(int characterId, int? fittingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> DetachFitAsync(int characterId, CancellationToken cancellationToken = default)
        {
            _reading = _reading with { SelectedFit = null, MatchReason = ShipFitMatchReason.Detached };
            return Task.FromResult(Result.Success());
        }
    }

    /// <summary>
    /// ET-115. The stepper beside an enemy row is a row control, not a form field: the theme's default sizing gave
    /// it chevrons about twice the height of the name they belong to, in a window where every other button is the
    /// <c>pick</c> style. Measured against one of those buttons rather than against a number, so the two keep step.
    /// </summary>
    [AvaloniaFact]
    public async Task TheEnemyStepper_IsNoTallerThanTheWindowsOwnButtons()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await harness.StartWatchingAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        await harness.WriteLineAsync(ActivityWindowHarness.CombatLine(250, "Centii Servant"));
        await ActivityWindowHarness.WaitUntil(() => model.Enemies().EnemyObservations.Count > 0);

        ActivityWindow window = _Open(model, expanded: true);

        NumericUpDown stepper = Assert.Single(window.GetVisualDescendants().OfType<NumericUpDown>());
        Button reference = _Button(window, "StopRunButton");
        double name = stepper.FindAncestorOfType<Grid>()!.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Text == "Centii Servant").Bounds.Height;

        Assert.InRange(stepper.Bounds.Height, name, reference.Bounds.Height);
        window.Close();   // its clock is ticking; the collection's other tests share this UI thread.
    }

    [Fact]
    public void FitStats_UnreadableFit_SaysItCouldNotBeRead()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());

        model.Fit().ApplyFitStats(null, fitCouldBeRead: false);

        Assert.Equal("fit could not be read", model.Fit().FitVelocityText);
        Assert.Equal("fit could not be read", model.Fit().FitWarpSpeedText);
    }

    [Fact]
    public void InTheAbyss_LocationSaysWhyItIsEmpty()
    {
        var model = _Filled(ActivityKind.Abyssal);

        Assert.Contains("no location", model.Activity().HeaderSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pocket", model.Activity().LocationText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An abyssal pocket has no NPC bounty at all (ET-241, Jithran flying one with Raymond, 2026-09-11) —
    /// unlike a data or relic site, which merely usually has none, the window draws no BOUNTY section for it at
    /// all, rather than one that always says so.</summary>
    [Fact]
    public void InTheAbyss_ThereIsNoBountySectionAtAll()
    {
        var model = _Filled(ActivityKind.Abyssal);

        Assert.DoesNotContain(model.Sections, section => section is BountyWindowSectionViewModel);
    }

    /// <summary>
    /// A system tells you which Sansha Refuge you are looking at no better than its name does, so LOCATION carries
    /// the scan id behind the place when there is one (Raymond, 2026-09-03). Never behind "not known yet" — that
    /// line is about us — and never in the abyss, where a filament carries no signature at all.
    /// </summary>
    [Theory]
    [InlineData(ActivityKind.Site, "Shaggoth", "RUS-326", "Shaggoth (RUS-326)")]
    [InlineData(ActivityKind.Site, "Shaggoth", null, "Shaggoth")]
    [InlineData(ActivityKind.Site, null, "RUS-326", "not known yet")]
    [InlineData(ActivityKind.Abyssal, "Shaggoth", "RUS-326", "none — an abyssal pocket has no location")]
    public void TheLocationRow_NamesTheScanBehindThePlace(
        ActivityKind kind, string? system, string? signatureId, string expected)
    {
        var model = new ActivityWindowViewModel(kind, _Unused());
        ActivityWindowSectionViewModel activity = model.Activity();
        var changed = new List<string?>();
        activity.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        model.SolarSystem = system;
        model.SignatureId = signatureId;

        Assert.Equal(expected, activity.LocationText);
        // The value being right is half of it: without the notification the row never redraws.
        Assert.Contains(nameof(activity.LocationText), changed);
    }

    /// <summary>A row that would only report our own ignorance is not on screen at all — that rule is what took the
    /// catalogue lines out of ACTIVITY, and LOCATION follows it too.</summary>
    [Fact]
    public void BeforeAnyLocationIsKnown_TheLocationRowIsNotShown()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Site, _Unused());

        Assert.False(model.Activity().IsLocationShown);
    }


    // ── The clock ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AbyssalClock_CountsDownFromTheAnchor_AndEndsAtTheDeadline()
    {
        var model = _Filled(ActivityKind.Abyssal);
        model.Refresh(Anchor.AddMinutes(6));

        Assert.Equal("TIME LEFT", model.ClockLabel);
        Assert.Equal("14:00", model.ClockText);
        Assert.False(model.IsClockWarning);
        Assert.False(model.IsClockCritical);

        // END is the deadline, not the moment the last pilot got out: at RunLimit the ship and the pod are gone.
        Assert.Equal((Anchor + AbyssalSpace.RunLimit).ToLocalTime().ToString("HH:mm:ss"), model.EndText);
    }

    [Fact]
    public void AbyssalClock_TurnsAmberAtFiveMinutes_ThenRedAtTwo_AndStaysRedPastTheDeadline()
    {
        var model = _Filled(ActivityKind.Abyssal);

        model.Refresh(Anchor.AddMinutes(15).AddSeconds(30));
        Assert.True(model.IsClockWarning);
        Assert.False(model.IsClockCritical);

        model.Refresh(Anchor.AddMinutes(18).AddSeconds(30));
        Assert.False(model.IsClockWarning);
        Assert.True(model.IsClockCritical);

        // Past the deadline we are already wrong about something. A lifted null comparison would have reported that
        // in the resting colour, which is the one state this readout must never be quiet about.
        model.Refresh(Anchor.AddMinutes(21));
        Assert.Equal("--:--", model.ClockText);
        Assert.True(model.IsClockCritical);
    }

    [Fact]
    public void AbyssalClock_MatchesTheCountdownAbyssalSpaceDescribes()
    {
        var model = _Filled(ActivityKind.Abyssal);
        DateTime now = Anchor.AddMinutes(7).AddSeconds(13);
        model.Refresh(now);

        // One countdown, not two. The window shows the figure without Describe's wrapper and without its "+" (the
        // sign moved to the hint under it), so the two must not be able to drift apart on the number itself.
        Assert.Equal($"Abyssal ({model.ClockText}+)", AbyssalSpace.Describe(null, Anchor, now));
    }

    [Fact]
    public void AbyssalClock_WithNoAnchorYet_SaysSoRatherThanShowingAFullRun()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());
        model.Refresh(Anchor);

        Assert.Equal("--:--", model.ClockText);
        Assert.Equal("not started", model.StartText);
        Assert.Equal("not started", model.EndText);
    }

    [Fact]
    public void SiteClock_CountsUp_AndHasNoDeadline()
    {
        var model = _Filled(ActivityKind.Site);
        model.Refresh(Anchor.AddMinutes(73).AddSeconds(4));

        Assert.Equal("ELAPSED", model.ClockLabel);
        Assert.Equal("73:04", model.ClockText);   // past the hour rather than wrapping — a site is bounded by nothing
        Assert.Equal("still running", model.EndText);
    }

    /// <summary>
    /// ET-246: a pocket's run starts on this pilot's own way in. The envelope used to take the earliest fleet anchor
    /// as this window's start, so a member's clock began the moment the commander jumped — the anchors are each
    /// member's own entry, and here they are only counted.
    /// </summary>
    [Fact]
    public void FleetEnvelope_CountsOnlyAnchoredMembers_AndStartsNobodysRun()
    {
        DateTime received = Anchor.AddMinutes(8);
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());

        model.ApplyFleetEnvelope(
        [
            new MetricSample(1, 7, MetricKind.Location, 0, 1_000_000, AbyssalAnchorMs: 700_000),
            new MetricSample(2, 7, MetricKind.Location, 0, 5_000_000, AbyssalAnchorMs: 4_731_000),
            new MetricSample(3, 7, MetricKind.Location, 0, 1_000_000)
        ], received);

        Assert.Equal(ActivityRunState.NotStarted, model.RunState);
        Assert.Null(model.AnchorUtc);
        Assert.Equal(2, model.AnchoredFleetMemberCount);
        Assert.Equal(3, model.FleetMemberCount);
        Assert.Contains("2 of 3", model.Fleet().HeaderSummary);
    }

    [Fact]
    public void FleetEnvelope_ExpiredAnchorsStillCountUnlikeMissingAnchors()
    {
        DateTime received = Anchor.AddMinutes(8);
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());

        model.ApplyFleetEnvelope(
        [
            new MetricSample(1, 7, MetricKind.Location, 0, 1_500_000, AbyssalAnchorMs: 300_000),
            new MetricSample(2, 7, MetricKind.Location, 0, 1_000_000)
        ], received);

        Assert.Equal(1, model.AnchoredFleetMemberCount);
        Assert.Equal(2, model.FleetMemberCount);
    }

    [Fact]
    public void SiteRuns_CountFleetMembersWithoutAcceptingAutomaticAnchors()
    {
        MetricSample automaticAnchor = new(1, 7, MetricKind.Location, 0, 1_000_000, AbyssalAnchorMs: 700_000);
        var site = new ActivityWindowViewModel(ActivityKind.Site, _Unused());

        site.ApplyFleetEnvelope(
        [
            automaticAnchor,
            new MetricSample(2, 7, MetricKind.Location, 0, 1_000_000)
        ], Anchor.AddMinutes(8));

        Assert.Equal(ActivityRunState.NotStarted, site.RunState);
        Assert.Null(site.AnchorUtc);
        Assert.Equal(2, site.FleetMemberCount);
        Assert.Contains("based on 2 members sharing their location", site.Fleet().HeaderSummary);
    }

    /// <summary>
    /// <c>FleetMembers</c> is one row per member that sent a location sample — each their own. "sharing a location"
    /// read as one place shared between them, and under the chip stood RaymondKrah in Amarr and Jithran in Shaggoth
    /// (Raymond, 2026-09-03). The chip says what was measured: they each share theirs.
    /// </summary>
    [Theory]
    [InlineData(ActivityKind.Site, "based on 2 members sharing their location")]
    [InlineData(ActivityKind.Abyssal, "based on 0 of 2 members sharing their location")]
    public void TheFleetChip_CountsMembersSharingTheirOwnLocation_NotMembersInOnePlace(
        ActivityKind kind, string expected)
    {
        var model = new ActivityWindowViewModel(kind, _Unused());

        model.ApplyFleetEnvelope(
        [
            new MetricSample(1, 7, MetricKind.Location, 0, 1_000_000, Text: "Amarr"),
            new MetricSample(2, 7, MetricKind.Location, 0, 1_000_000, Text: "Shaggoth")
        ], Anchor);

        Assert.Equal(expected, model.FleetStatusText);
        Assert.DoesNotContain("sharing a location", model.FleetStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualStart_PreservesKnownFleetMembership()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());

        model.ApplyFleetEnvelope(
        [
            new MetricSample(1, 7, MetricKind.Location, 0, 1_000_000, AbyssalAnchorMs: 700_000),
            new MetricSample(2, 7, MetricKind.Location, 0, 1_000_000)
        ], Anchor.AddMinutes(8));
        model.StartManualRun(Anchor.AddMinutes(2));

        Assert.Equal(2, model.FleetMemberCount);
        Assert.Equal(1, model.AnchoredFleetMemberCount);
    }

    [Fact]
    public void StoppedRuns_RejectAutomaticAnchors()
    {
        MetricSample automaticAnchor = new(1, 7, MetricKind.Location, 0, 1_000_000, AbyssalAnchorMs: 700_000);
        var stopped = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());
        stopped.StartManualRun(Anchor.AddMinutes(2));
        stopped.StopRun(Anchor.AddMinutes(4));

        stopped.ApplyFleetEnvelope([automaticAnchor], Anchor.AddMinutes(8));

        Assert.Equal(ActivityRunState.Stopped, stopped.RunState);
        Assert.Equal(Anchor.AddMinutes(2), stopped.AnchorUtc);
    }

    [Fact]
    public void FleetEnvelope_WithoutAnchorsReportsTheGapWithoutStartingARun()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());

        model.ApplyFleetEnvelope([new MetricSample(1, 7, MetricKind.Location, 0, 1_000_000)], Anchor.AddMinutes(8));

        Assert.Equal(ActivityRunState.NotStarted, model.RunState);
        Assert.Equal(0, model.AnchoredFleetMemberCount);
        Assert.Equal(1, model.FleetMemberCount);
    }

    [Fact]
    public void ManualRun_AutomaticEnvelopeDoesNotSilentlyMoveTheStart()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());
        DateTime manualStart = Anchor.AddMinutes(2);
        model.StartManualRun(manualStart);

        model.ApplyFleetEnvelope(
        [new MetricSample(1, 7, MetricKind.Location, 0, 1_000_000, AbyssalAnchorMs: 100_000)], Anchor.AddMinutes(8));

        Assert.Equal(ActivityRunState.Running, model.RunState);
        Assert.Equal(manualStart, model.AnchorUtc);
        Assert.Equal(manualStart.ToLocalTime().ToString("HH:mm:ss"), model.StartText);
    }

    /// <summary>A fleet mate already in the pocket leaves this window armed and waiting for this pilot (ET-246): START
    /// stays on offer and the window says it is armed — no clock of anybody else's is put on it.</summary>
    [Fact]
    public void AFleetMateInThePocket_LeavesThisWindowArmed_WithStartOnOffer()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());

        model.ApplyFleetEnvelope(
        [new MetricSample(1, 7, MetricKind.Location, 0, 1_000_000, AbyssalAnchorMs: 700_000)], Anchor.AddMinutes(8));

        Assert.Equal("not started", model.RunOriginText);
        Assert.True(model.IsStartButtonVisible);
        Assert.False(model.IsStopButtonVisible);
        Assert.True(model.IsArmedShown);

        model.StartManualRun(Anchor.AddMinutes(5));

        Assert.Equal("manual", model.RunOriginText);
        Assert.Equal(Anchor.AddMinutes(5), model.AnchorUtc);
        Assert.False(model.IsStartButtonVisible);
        Assert.False(model.IsArmedShown);
    }

    // ── The four buttons, against every state the run can be in ─────────────────────────────────────

    // SAVE joins STOP and DISCARD while Running (ET-225): one click stops the clock at that moment and saves,
    // gated the same way STOP itself is rather than opening a fourth authority to reason about.
    [Theory]
    [InlineData(ActivityRunState.NotStarted, true, false, false, false)]
    [InlineData(ActivityRunState.Running, false, true, true, true)]
    [InlineData(ActivityRunState.Stopped, true, false, true, true)]
    public void TheRunControls_ShowExactlyWhatTheStateAllows(ActivityRunState state,
        bool start, bool stop, bool save, bool discard)
    {
        var model = _InState(state);

        Assert.Equal(state, model.RunState);
        Assert.Equal(start, model.IsStartButtonVisible);
        Assert.Equal(stop, model.IsStopButtonVisible);
        Assert.Equal(save, model.IsSaveButtonVisible);
        Assert.Equal(discard, model.IsDiscardButtonVisible);

        // Start and stop are the same slot seen from two sides, and the window puts them in one cell: both on at
        // once would draw them over each other.
        Assert.False(model.IsStartButtonVisible && model.IsStopButtonVisible);
    }

    [Fact]
    public void AStoppedRunCanAlwaysBeSaved_WhetherOrNotARunRowExistsYet()
    {
        // Saving is the whole point of stopping, so the button hangs on the state. A stopped run with nowhere to
        // save to is a fault the command reports — never a button quietly missing from the corner.
        var model = _InState(ActivityRunState.Stopped);

        Assert.Null(model.RunId);
        Assert.True(model.IsSaveButtonVisible);
    }

    [Fact]
    public void SavingDoesNotMoveTheButtons_ASavedRunIsStillAStoppedOne()
    {
        var model = _InState(ActivityRunState.Stopped);
        (bool start, bool stop, bool save, bool discard) before =
            (model.IsStartButtonVisible, model.IsStopButtonVisible, model.IsSaveButtonVisible, model.IsDiscardButtonVisible);

        model.SaveRunCommand.Execute(null);

        Assert.Equal(ActivityRunState.Stopped, model.RunState);
        Assert.Equal(before,
            (model.IsStartButtonVisible, model.IsStopButtonVisible, model.IsSaveButtonVisible, model.IsDiscardButtonVisible));
    }

    [Theory]
    [InlineData(ActivityRunState.NotStarted)]
    [InlineData(ActivityRunState.Running)]
    [InlineData(ActivityRunState.Stopped)]
    public void WithoutTheCommand_TheThreeSharedControlsGoAway_AndSayWhy(ActivityRunState state)
    {
        // A shared run — a group code is what makes it one, and only a shared run has a commander to be denied by
        // (ET-152). Without it these three belong to the pilot in front of them.
        var denied = _InState(state);
        denied.GroupCode = "HF-7Q2";
        denied.ApplyFleetCommand(fleetId: 7, fleetCommanderCharacterId: 1, actingCharacterId: 2);

        Assert.False(denied.IsStartButtonVisible);
        Assert.False(denied.IsStopButtonVisible);
        Assert.False(denied.IsDiscardButtonVisible);
        Assert.True(denied.IsCommandStatusShown);
        Assert.False(string.IsNullOrWhiteSpace(denied.CommandStatusText));

        // Saving is this pilot's own part of the run and is never the FC's to withhold.
        Assert.Equal(state == ActivityRunState.Stopped, denied.IsSaveButtonVisible);

        var unknown = _InState(state);
        unknown.GroupCode = "HF-7Q2";
        unknown.ApplyFleetCommand(fleetId: 7, fleetCommanderCharacterId: null, actingCharacterId: 2);

        Assert.False(unknown.IsStartButtonVisible);
        Assert.False(unknown.IsStopButtonVisible);
        Assert.False(unknown.IsDiscardButtonVisible);
        Assert.True(unknown.IsCommandStatusShown);
        Assert.NotEqual(denied.CommandStatusText, unknown.CommandStatusText);
    }

    private static ActivityWindowViewModel _InState(ActivityRunState state)
    {
        var model = new ActivityWindowViewModel(ActivityKind.Site, _Unused());
        if (state is ActivityRunState.NotStarted)
            return model;

        model.StartManualRun(Anchor);
        if (state is ActivityRunState.Stopped)
            model.StopRun(Anchor.AddMinutes(9));

        return model;
    }

    [Fact]
    public void ManualRun_StartStopAndRestart_UsesThreeDistinctStatesAndKeepsStoppedFigures()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Site, _Unused());

        Assert.Equal(ActivityRunState.NotStarted, model.RunState);
        model.StartManualRun(Anchor);
        model.Refresh(Anchor.AddMinutes(9));
        model.StopRun(Anchor.AddMinutes(9));
        model.Refresh(Anchor.AddMinutes(12));

        Assert.Equal(ActivityRunState.Stopped, model.RunState);
        Assert.Equal("09:00", model.ClockText);
        Assert.Equal(Anchor.AddMinutes(9).ToLocalTime().ToString("HH:mm:ss"), model.EndText);

        model.StartManualRun(Anchor.AddMinutes(12));
        Assert.Equal(ActivityRunState.Running, model.RunState);
        Assert.Equal(Anchor.AddMinutes(12), model.AnchorUtc);
    }

    // ── AC-3 — weather and tier, in two clicks, remembered ──────────────────────────────────────────

    [AvaloniaFact]
    public async Task WeatherAndTier_SurviveANewWindow()
    {
        using var instance = TestClientInstance.Create();

        var first = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        await first.LoadAsync();
        Assert.Null(first.WeatherIndex);
        Assert.Null(first.TierIndex);

        await first.Activity().SelectWeatherCommand.ExecuteAsync(3);
        await first.Activity().SelectTierCommand.ExecuteAsync(5);

        var second = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        await second.LoadAsync();

        Assert.Equal(3, second.WeatherIndex);
        Assert.Equal(5, second.TierIndex);
        Assert.Equal("Firestorm", second.Weather?.Name);
        Assert.True(second.Activity().WeatherChoices[3].IsSelected);
        Assert.True(second.Activity().TierChoices[5].IsSelected);

        await second.Activity().ClearWeatherAndTierCommand.ExecuteAsync(null);

        var third = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        await third.LoadAsync();
        Assert.Null(third.WeatherIndex);
        Assert.Null(third.TierIndex);
    }

    [AvaloniaFact]
    public async Task ARememberedChoiceThatNoLongerAddressesAnything_ReadsAsUnset()
    {
        using var instance = TestClientInstance.Create();
        var settings = instance.Services.GetRequiredService<ISettingRepository>();
        await settings.UpsertAsync(ActivityWindowSectionViewModel.WeatherSettingKey, "9");
        await settings.UpsertAsync(ActivityWindowSectionViewModel.TierSettingKey, "not a number");

        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        await model.LoadAsync();

        Assert.Null(model.WeatherIndex);
        Assert.Null(model.TierIndex);
    }

    [Fact]
    public void TheClockDoesNotWaitForWeatherOrTier()
    {
        DateTime now = Anchor.AddMinutes(4).AddSeconds(21);

        var unset = _Filled(ActivityKind.Abyssal);
        unset.Refresh(now);

        var set = _Filled(ActivityKind.Abyssal);
        set.WeatherIndex = 1;
        set.TierIndex = 2;
        set.Refresh(now);

        Assert.Equal(set.ClockText, unset.ClockText);
        Assert.Equal(set.EndText, unset.EndText);

        // And the header asks for the two rather than leaving the reader to notice the gap.
        Assert.True(unset.NeedsWeatherAndTier);
        Assert.False(set.NeedsWeatherAndTier);
    }

    [AvaloniaTheory]
    [InlineData(ActivityKind.Mission, false)]
    [InlineData(ActivityKind.Mission, true)]
    [InlineData(ActivityKind.Abyssal, false)]
    public void HeaderChips_ShowTheMissionLevelOrAbyssalWeatherAndTier(ActivityKind kind, bool levelArrivesAfterOpening)
    {
        var model = new ActivityWindowViewModel(kind, _Unused()) { WeatherIndex = 0, TierIndex = 0 };
        if (!levelArrivesAfterOpening)
            model.MissionLevel = 4;

        ActivityWindow window = _Open(model, expanded: false);

        if (levelArrivesAfterOpening)
        {
            model.MissionLevel = 4;
            Dispatcher.UIThread.RunJobs();
        }

        Border level = window.FindControl<Border>("MissionLevelChip")
            ?? throw new Xunit.Sdk.XunitException("the mission level chip was not rendered");
        Border tier = window.FindControl<Border>("TierChip")
            ?? throw new Xunit.Sdk.XunitException("the tier chip was not rendered");
        Border weather = window.FindControl<Border>("WeatherChip")
            ?? throw new Xunit.Sdk.XunitException("the weather chip was not rendered");

        Assert.Equal(kind == ActivityKind.Mission, level.IsVisible);
        Assert.Equal(kind == ActivityKind.Abyssal, tier.IsVisible);
        Assert.Equal(kind == ActivityKind.Abyssal, weather.IsVisible);
        if (kind == ActivityKind.Mission)
            Assert.Equal("Level 4", Assert.Single(level.GetVisualDescendants().OfType<TextBlock>()).Text);

        window.Close();
    }

    [Fact]
    public void ThePickerFoldsAwayOnceAnswered_AndReopensOnRequest()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());
        Assert.True(model.Activity().IsPickerShown);

        model.WeatherIndex = 1;
        Assert.True(model.Activity().IsPickerShown);   // half an answer is not an answer

        model.TierIndex = 2;
        Assert.False(model.Activity().IsPickerShown);

        model.Activity().OpenPickerCommand.Execute(null);
        Assert.True(model.Activity().IsPickerShown);
    }

    [Fact]
    public void ThePickerOffersFiveWeathersAndSevenTiers()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());

        Assert.Equal(5, model.Activity().WeatherChoices.Count);
        Assert.Equal(7, model.Activity().TierChoices.Count);
        Assert.All(model.Activity().WeatherChoices, choice => Assert.False(string.IsNullOrWhiteSpace(choice.Tooltip)));
    }

    // ── The loot strategy: a label you can set, not one that only reports it is unset ────────────────

    [Fact]
    public void EveryKindOffersTheStrategiesItActuallyLootsBy()
    {
        var abyssal = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());
        var site = new ActivityWindowViewModel(ActivityKind.Site, _Unused());

        Assert.NotEmpty(abyssal.Activity().LootStrategyChoices);
        Assert.NotEmpty(site.Activity().LootStrategyChoices);
        Assert.NotEqual(
            abyssal.Activity().LootStrategyChoices.Select(choice => choice.Label),
            site.Activity().LootStrategyChoices.Select(choice => choice.Label));
        Assert.All(abyssal.Activity().LootStrategyChoices, choice => Assert.False(choice.IsSelected));
        // Leaving the other cans is a site's move: an abyssal pocket is instanced, so there is nobody to leave them to.
        Assert.Contains(RunLootStrategy.CherryPicked, site.Activity().LootStrategies);
        Assert.DoesNotContain(RunLootStrategy.CherryPicked, abyssal.Activity().LootStrategies);
    }

    [AvaloniaFact]
    public async Task TheLootStrategy_IsSet_Unset_AndRemembered()
    {
        using var instance = TestClientInstance.Create();

        var first = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        await first.LoadAsync();
        Assert.Null(first.Activity().LootStrategy);

        await first.Activity().SelectLootStrategyCommand.ExecuteAsync(1);
        Assert.Equal(RunTypeCatalogue.SiteLootStrategies[1], first.Activity().LootStrategy);
        Assert.True(first.Activity().LootStrategyChoices[1].IsSelected);

        var second = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        await second.LoadAsync();
        Assert.Equal(RunTypeCatalogue.SiteLootStrategies[1], second.Activity().LootStrategy);
        Assert.True(second.Activity().LootStrategyChoices[1].IsSelected);

        // The row has no other way back: pressing the answer again is how you take it back.
        await second.Activity().SelectLootStrategyCommand.ExecuteAsync(1);
        Assert.Null(second.Activity().LootStrategy);

        var third = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        await third.LoadAsync();
        Assert.Null(third.Activity().LootStrategy);
    }

    /// <summary>One key per kind. On the shared key the two kinds overwrote each other's answer, so the second
    /// window of an evening opened on whatever the other kind had been asked last.</summary>
    [AvaloniaFact]
    public async Task EachKindRemembersItsOwnStrategy_NotTheLastOneAnswered()
    {
        using var instance = TestClientInstance.Create();

        var site = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        await site.LoadAsync();
        await site.Activity().SelectLootStrategyCommand.ExecuteAsync(0);

        var abyssal = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        await abyssal.LoadAsync();
        Assert.Null(abyssal.Activity().LootStrategy);
        Assert.All(abyssal.Activity().LootStrategyChoices, choice => Assert.False(choice.IsSelected));
        await abyssal.Activity().SelectLootStrategyCommand.ExecuteAsync(2);

        var nextSite = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        await nextSite.LoadAsync();
        Assert.Equal(RunTypeCatalogue.SiteLootStrategies[0], nextSite.Activity().LootStrategy);
    }

    /// <summary>Set, and stored — the whole point of the row. Both ways in: chosen before there is a run to write it
    /// to, and chosen on one that is already going.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheChosenStrategy_IsOnTheStoredRun(bool chosenBeforeStart)
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ActivityWindowViewModel window = await harness.OpenAsync(ActivityKind.Site);
        int cherryPicked = window.Activity().LootStrategies.ToList().IndexOf(RunLootStrategy.CherryPicked);

        if (chosenBeforeStart)
            await window.Activity().SelectLootStrategyCommand.ExecuteAsync(cherryPicked);
        await window.StartRunCommand.ExecuteAsync(null);
        if (!chosenBeforeStart)
            await window.Activity().SelectLootStrategyCommand.ExecuteAsync(cherryPicked);

        window.StopRun(DateTime.UtcNow);
        await window.SaveRunCommand.ExecuteAsync(null);

        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run stored = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        Assert.Equal(RunLootStrategy.CherryPicked, stored.LootStrategy);
    }

    /// <summary>The case ET-179 was written for: the pilot answered and then never pressed SAVE. The chip writes the
    /// answer onto the run there and then, so the save the app does a day later finds it already on the row.</summary>
    [AvaloniaFact]
    public async Task AStrategyOnARunNobodyFinished_SurvivesTheAppsOwnSave()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ActivityWindowViewModel window = await harness.OpenAsync(ActivityKind.Site);

        await window.StartRunCommand.ExecuteAsync(null);
        await window.Activity().SelectLootStrategyCommand.ExecuteAsync(0);
        window.StopRun(DateTime.UtcNow);

        await harness.Services.GetRequiredService<Shared.Cqrs.IDispatcher>()
            .Send(new SaveRunsLeftUnfinishedCommand(DateTime.UtcNow.AddDays(2)), cancellationToken);

        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run stored = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        Assert.NotNull(stored.AutoSavedAtUtc);
        Assert.Equal(RunTypeCatalogue.SiteLootStrategies[0], stored.LootStrategy);
    }

    [Fact]
    public void ThePenaltyIsShownAsTheBandItRollsIn_NotAsANumberPerTier()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused()) { WeatherIndex = 4, TierIndex = 2 };
        Assert.Contains("-30% or -50%", model.Activity().WeatherEffectText);

        model.TierIndex = 5;
        Assert.Contains("-50% or -70%", model.Activity().WeatherEffectText);
    }

    // ── AC-6 — the ISK figures name their own source, and claim nothing else ────────────────────────

    [Theory]
    [InlineData(ActivityKind.Abyssal)]
    [InlineData(ActivityKind.Site)]
    public void NoLabelEverImpliesTheWindowValuedTheLootItself(ActivityKind kind)
    {
        var model = _Filled(kind);
        model.WeatherIndex = 2;
        model.TierIndex = 3;
        model.Refresh(Anchor.AddMinutes(3));

        string[] forbidden = ["jita", "markt", "market", "waardering", "appraisal"];
        foreach (var text in _ExposedText(model))
            foreach (var word in forbidden)
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);

        // The caption used to claim the totals WERE the copied ISK column, and went on claiming it after the copied
        // ISK stopped being kept at all. It names the price lookup now — and nothing else, because the warning that
        // the per-row figure was the copied column stopped being true when the rows started being valued the same
        // way the totals are. A caption that names the wrong source makes a right number look doubtful.
        Assert.DoesNotContain("Prices are the clipboard column", model.Loot().IskLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("copied column", model.Loot().IskLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("type id", model.Loot().IskLabel, StringComparison.OrdinalIgnoreCase);
    }

    // ── AC-7 — the four faction palettes reach this window ──────────────────────────────────────────


    [Fact]
    public void EveryBrushInTheWindowIsAResourceKey_NotALiteral()
    {
        // An OverlayWindow does not bind its own brushes to resource observables the way a ChromedWindow does, so one
        // "#rrggbb" left in this file is one thing that silently stops following the faction — and looks perfectly
        // correct in a screenshot of the default palette.
        string markup = File.ReadAllText(_SourcePath("EveUtils.Client/Views/ActivityWindow.axaml"));

        Assert.DoesNotContain("=\"#", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFactionKeyInTheWindowIsBoundLate_AndEveryStaticOneIsNeutral()
    {
        // The counterproof the render below cannot give on its own. The accent reaches the screen through shared
        // theme classes too, so "the accent is on the window" stays true even after a brush in this file has been
        // pinned to whichever palette happened to be loaded when it parsed. This reads the rule instead: a key that
        // differs per faction must be bound late, and a key bound once must not be one of those.
        string markup = File.ReadAllText(_SourcePath("EveUtils.Client/Views/ActivityWindow.axaml"));
        var swappable = _FactionKeys();

        foreach (var key in _Keys(markup, "StaticResource"))
            Assert.False(swappable.Contains(key),
                $"{key} differs per faction, so StaticResource freezes it at whichever palette parsed first");

        foreach (var key in _Keys(markup, "DynamicResource"))
            Assert.True(swappable.Contains(key),
                $"{key} is bound late but is not in Themes/Factions — it is either a typo or a neutral key");
    }

    [AvaloniaFact]
    public void TheAccentOnScreen_IsTheAccentOfTheAppliedFaction()
    {
        using var instance = TestClientInstance.Create();
        var theme = instance.Services.GetRequiredService<IThemeService>();

        (FactionTheme Faction, Color Accent)[] palettes =
        [
            (FactionTheme.Gallente, Color.Parse("#FF7EE0BB")),
            (FactionTheme.Amarr, Color.Parse("#FFF3D488")),
            (FactionTheme.Caldari, Color.Parse("#FF8FC6F0")),
            (FactionTheme.Minmatar, Color.Parse("#FFE68676"))
        ];

        try
        {
            foreach (var (faction, accent) in palettes)
            {
                theme.Apply(faction);

                var window = _Open(_Set(_Running(ActivityKind.Abyssal)), expanded: true);
                var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);

                var painted = _Colours(frame!);
                Assert.True(painted.Contains(accent),
                    $"{faction}'s accent is nowhere on the window — it is still wearing another palette");

                foreach (var (other, otherAccent) in palettes.Where(palette => palette.Faction != faction))
                    Assert.False(painted.Contains(otherAccent),
                        $"{other}'s accent is still on screen after applying {faction}");

                OverlayShots.Capture(window, $"eveutils-activity-{faction}".ToLowerInvariant());
                window.Close();
            }
        }
        finally
        {
            theme.Apply(FactionTheme.Gallente);
        }
    }

    // ── The window itself ───────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void TheWindowRenders_FoldedShut_Open_AndStillAsking()
    {
        var model = _Set(_Running(ActivityKind.Abyssal));

        var shut = _Open(model, expanded: false);
        Assert.NotNull(shut.CaptureRenderedFrame());
        OverlayShots.Capture(shut, "eveutils-activity-shut");
        shut.Close();

        var open = _Open(model, expanded: true);
        Assert.NotNull(open.CaptureRenderedFrame());
        OverlayShots.Capture(open, "eveutils-activity-open");
        open.Close();

        // The state the window opens in on a fresh run: the clock already running, the header asking for the two
        // things nothing can detect for it.
        var asking = _Open(_Running(ActivityKind.Abyssal), expanded: true);
        Assert.NotNull(asking.CaptureRenderedFrame());
        OverlayShots.Capture(asking, "eveutils-activity-unset");
        asking.Close();
    }

    [AvaloniaFact]
    public void TheWindowRenders_NotStarted_Running_AndStopped()
    {
        var notStarted = _Open(new ActivityWindowViewModel(ActivityKind.Site, _Unused()), expanded: true);
        Assert.NotNull(notStarted.CaptureRenderedFrame());
        _AssertButtons(notStarted, start: true, stop: false, save: false, discard: false);
        OverlayShots.Capture(notStarted, "eveutils-activity-not-started");
        notStarted.Close();

        var runningModel = new ActivityWindowViewModel(ActivityKind.Site, _Unused());
        runningModel.StartManualRun(DateTime.UtcNow.AddMinutes(-6));
        var running = _Open(runningModel, expanded: true);
        Assert.NotNull(running.CaptureRenderedFrame());
        // SAVE joins STOP and DISCARD while Running (ET-225): the block still does not move, cell 2 no longer empty.
        _AssertButtons(running, start: false, stop: true, save: true, discard: true);
        OverlayShots.Capture(running, "eveutils-activity-running");
        running.Close();

        runningModel.StopRun(DateTime.UtcNow);
        var stopped = _Open(runningModel, expanded: true);
        Assert.NotNull(stopped.CaptureRenderedFrame());
        _AssertButtons(stopped, start: true, stop: false, save: true, discard: true);
        OverlayShots.Capture(stopped, "eveutils-activity-stopped");
        stopped.Close();
    }

    /// <summary>The four controls as the window actually renders them, and the cells they sit in — equal width and
    /// equal spacing is what stops the block moving under the pointer when the run changes state. The width itself
    /// is the markup's; what this holds is that all four share it.</summary>
    private static void _AssertButtons(ActivityWindow window, bool start, bool stop, bool save, bool discard)
    {
        Assert.Equal(start, _Button(window, "StartRunButton").IsVisible);
        Assert.Equal(stop, _Button(window, "StopRunButton").IsVisible);
        Assert.Equal(save, _Button(window, "SaveRunButton").IsVisible);
        Assert.Equal(discard, _Button(window, "DiscardRunButton").IsVisible);

        var cells = new[] { "StartRunButton", "StopRunButton", "SaveRunButton", "DiscardRunButton" }
            .Select(name => _Button(window, name))
            .ToList();

        // No button carries a margin of its own and none sizes itself: the row's cells do both, which is what
        // makes the widths and the gaps equal whichever of them is on.
        Assert.All(cells, button => Assert.Equal(HorizontalAlignment.Stretch, button.HorizontalAlignment));
        Assert.All(cells, button => Assert.Equal(new Thickness(0), button.Margin));

        Assert.Equal(Grid.GetColumn(cells[0]), Grid.GetColumn(cells[1]));            // start and stop, one slot
        Assert.NotEqual(Grid.GetColumn(cells[2]), Grid.GetColumn(cells[3]));         // save and discard, two

        var row = Assert.IsType<Grid>(cells[0].Parent);
        var widths = new[] { 0, 2, 3 }.Select(column => row.ColumnDefinitions[column].Width).Distinct().ToList();
        Assert.Single(widths);
        Assert.True(row.ColumnDefinitions[1].Width.Value > 0, "the group boundary between steering and ending is gone");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A provider nothing in these tests reaches into: only the setting round-trip touches the client DI,
    /// and it uses a real <see cref="TestClientInstance"/>.</summary>
    // ── The site, described by what it is — never by the shape of our catalogue ─────────────────────

    [Fact]
    public void AMatchedSiteThatDemandsNothing_SaysOnlyItsName_NotWhatTheCatalogueIsMissing()
    {
        var model = _Site(_Entry("Haunted Yard"));

        Assert.Equal("Haunted Yard", model.Activity().SignatureSiteText);
        Assert.Equal("Haunted Yard", model.Activity().HeaderSummary);
        Assert.False(model.Activity().HasShipRestriction);
        Assert.Null(model.Activity().ShipRestrictionText);
    }

    // The branch that costs a ship if it collapses into the unrestricted one: a handful of the catalogue's type
    // lists state their restriction per hull, so a restricted site can resolve to no groups at all. It names no
    // hulls, so the SHIPS row has nothing to add — but the site line still says you are restricted.
    [Fact]
    public void ARestrictedSiteWhoseAllowListResolvesToNoGroups_StaysRestricted_AndIsNeverReadAsAnythingGoes()
    {
        var model = _Site(_Entry("Sleeper Cache", restricted: true));

        Assert.Equal("Sleeper Cache — ship-restricted", model.Activity().SignatureSiteText);
        Assert.False(model.Activity().HasShipRestriction);
        Assert.Equal("Sleeper Cache · ship-restricted", model.Activity().HeaderSummary);
    }

    [Fact]
    public void ASiteThatNamesItsHulls_PutsThemInTheirOwnRow()
    {
        var model = _Site(_Entry("Limited Sleeper Cache", groups: [new SdeGroup(25, 6, "Frigate", true)]));

        Assert.True(model.Activity().HasShipRestriction);
        Assert.Equal("Frigate", model.Activity().ShipRestrictionText);
        Assert.Equal("Limited Sleeper Cache — ship-restricted", model.Activity().SignatureSiteText);
    }

    /// <summary>ET-263: a homefront's T1-only cruisers (here: three fixture frigates standing in for the group) come
    /// back grouped under the group's own name once the SDE can resolve it, refined to "(T1 only)" because the
    /// included set is exactly that group's published Tech I census — never the loose includedTypeIDs names ET-232
    /// first showed.</summary>
    [Fact]
    public void IndividuallyIncludedHulls_AreGroupedAndFlaggedTechIOnly()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()
            .Add(621, "Rifter", 25, categoryId: 6, groupName: "Frigate")
            .Add(630, "Merlin", 25, categoryId: 6, groupName: "Frigate")
            .Add(640, "Punisher", 25, categoryId: 6, groupName: "Frigate"));
        var model = new ActivityWindowViewModel(ActivityKind.Site, services.BuildServiceProvider())
        {
            SignatureGroup = "Combat Site",
            SignatureName = "Raid: Hall of Sacrifice",
            MatchedSites = [_Entry("Raid: Hall of Sacrifice", includedTypes:
            [
                new SdeShipHull(621, "Rifter", 25), new SdeShipHull(630, "Merlin", 25), new SdeShipHull(640, "Punisher", 25)
            ])]
        };

        Assert.True(model.Activity().HasShipRestriction);
        Assert.Equal("Frigate (T1 only)", model.Activity().ShipRestrictionText);
        Assert.Null(model.Activity().ShipRestrictionTooltip);
    }

    /// <summary>ET-232 AC-2: an excluded hull is never shown as allowed, even when it was individually included by
    /// another type list referenced by the same site — the exclude always wins. No <see cref="ISdeAccessor"/>
    /// registered here, so this also covers the fallback to the loose hull name ET-232 first showed.</summary>
    [Fact]
    public void AnIndividuallyExcludedHull_IsNeverShownAsAllowed()
    {
        var model = _Site(_Entry("Raid: Hall of Sacrifice",
            includedTypes: [new SdeShipHull(621, "Rifter", 25), new SdeShipHull(640, "Punisher", 25)],
            excludedTypes: [new SdeShipHull(640, "Punisher", 25)]));

        Assert.Equal("Rifter", model.Activity().ShipRestrictionText);
        Assert.DoesNotContain("Punisher", model.Activity().ShipRestrictionText);
    }

    // ── ET-232: the SDE's own gameplayDescription, behind its own on-demand link ───────────────────────

    [Fact]
    public void AGameplayDescription_IsShownBehindItsOwnLink()
    {
        var model = _Site(_Entry("Raid: Hall of Sacrifice", gameplayDescription: "Bring 5 pilots."));

        Assert.True(model.Activity().HasGameplayDescription);
        Assert.Equal("Bring 5 pilots.", model.Activity().GameplayDescriptionText);
    }

    [Fact]
    public void NoGameplayDescription_ShowsNoLink()
    {
        var model = _Site(_Entry("Haunted Yard"));

        Assert.False(model.Activity().HasGameplayDescription);
        Assert.Null(model.Activity().GameplayDescriptionText);
    }

    [Fact]
    public void SeveralCatalogueEntriesSharingAName_ShowWhatTheyShare_AndSayNothingAboutTheCatalogue()
    {
        var model = _Site(
            _Entry("SCC Secure Key Storage", ded: 4, restricted: true),
            _Entry("SCC Secure Key Storage", ded: 8, groups: [new SdeGroup(25, 6, "Frigate", true)]));

        // Both are restricted, so that holds; the ratings disagree, so no DED is claimed. What is never said is
        // how many rows our own catalogue happens to carry — that is our problem, not the pilot's.
        Assert.Equal("SCC Secure Key Storage — ship-restricted", model.Activity().SignatureSiteText);
        Assert.DoesNotContain("catalogue", model.Activity().SignatureSiteText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entries", model.Activity().SignatureSiteText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DED", model.Activity().SignatureSiteText, StringComparison.Ordinal);

        // Which hulls is the open question, so the row that would answer it stays away rather than picking one.
        Assert.False(model.Activity().HasShipRestriction);
        Assert.Equal("SCC Secure Key Storage · ship-restricted", model.Activity().HeaderSummary);
    }

    [Fact]
    public void TheSiteLine_TheShutHeader_AndTheToast_AllDescribeTheSiteTheSameWay()
    {
        // One description, three readers. Two of them drifting apart is how the window ended up telling Raymond
        // about our catalogue while the toast told him about his site.
        var matches = new[] { _Entry("Angel Hideaway", ded: 3, restricted: true) };
        var model = _Site(matches);

        Assert.Equal($"Angel Hideaway — {SdeSiteDescription.DescribeCommon(matches)}", model.Activity().SignatureSiteText);
        Assert.Equal($"Angel Hideaway · {SdeSiteDescription.DescribeCommon(matches)}", model.Activity().HeaderSummary);
        Assert.Equal(SdeSiteDescription.DescribeMatches(matches), SdeSiteDescription.DescribeCommon(matches));
    }

    // ── Nothing on screen names one of our tickets ──────────────────────────────────────────────────

    [Theory]
    [InlineData(ActivityKind.Abyssal)]
    [InlineData(ActivityKind.Site)]
    public void NoTextTheUserCanRead_EverNamesATicket(ActivityKind kind)
    {
        var model = _Filled(kind);
        model.SignatureGroup = "Combat Site";
        model.SignatureName = "Sansha Hideaway";
        model.MatchedSites = [_Entry("Sansha Hideaway", ded: 4), _Entry("Sansha Hideaway", restricted: true)];
        model.StartManualRun(Anchor);
        model.Refresh(Anchor.AddMinutes(3));

        foreach (var text in _ExposedText(model).Concat(model.Activity().LootStrategyChoices.Select(choice => choice.Label)))
            Assert.DoesNotMatch(TicketNumber, text);
    }

    [Fact]
    public void NoMarkupTheUserCanRead_EverNamesATicket()
    {
        // Every view, not only this one: the two that reached the screen today were both plain Text="…", and the
        // comment above them is not what the user reads.
        foreach (var view in Directory.EnumerateFiles(_SourcePath("EveUtils.Client"), "*.axaml", SearchOption.AllDirectories))
            foreach (Match attribute in Regex.Matches(File.ReadAllText(view),
                         @"(?:Text|Content|ToolTip\.Tip|Header|Watermark|Title)\s*=\s*""([^""]*)"""))
                Assert.False(Regex.IsMatch(attribute.Groups[1].Value, TicketNumber),
                    $"{Path.GetFileName(view)} shows the reader a ticket number: {attribute.Value}");
    }

    private const string TicketNumber = @"\bET-\d";

    private static ActivityWindowViewModel _Site(params SdeSite[] matches) =>
        new(ActivityKind.Site, _Unused())
        {
            SignatureGroup = "Combat Site",
            SignatureName = matches[0].Name,
            MatchedSites = matches
        };

    private static SdeSite _Entry(string name, int? ded = null, bool restricted = false,
        IReadOnlyList<SdeGroup>? groups = null, IReadOnlyList<SdeShipHull>? includedTypes = null,
        IReadOnlyList<SdeShipHull>? excludedTypes = null, string? gameplayDescription = null) =>
        new(1263, name, null, null, null, null, null, ded,
            restricted || groups is not null || includedTypes is not null, groups ?? [], gameplayDescription,
            includedTypes, excludedTypes);

    private static IServiceProvider _Unused() => new ServiceCollection().BuildServiceProvider();

    private static ActivityWindowViewModel _Filled(ActivityKind kind) => _Filled(kind, Anchor);

    private static ActivityWindowViewModel _Filled(ActivityKind kind, DateTime anchorUtc) =>
        new(kind, _Unused())
        {
            AnchorUtc = anchorUtc,
            SolarSystem = kind == ActivityKind.Site ? "Aphend" : null   // a pocket genuinely has none
        };

    /// <summary>A run six minutes in on the clock the window itself will read. Anything anchored to a fixed date
    /// renders as a full twenty minutes, because Show() starts the timer and the timer uses the real now.</summary>
    private static ActivityWindowViewModel _Running(ActivityKind kind) =>
        _Filled(kind, DateTime.UtcNow.AddMinutes(-6));

    private static ActivityWindowViewModel _Set(ActivityWindowViewModel model)
    {
        model.WeatherIndex = 4;
        model.TierIndex = 3;
        model.Refresh(DateTime.UtcNow);
        return model;
    }

    private static ActivityWindow _Open(ActivityWindowViewModel model, bool expanded)
    {
        foreach (var section in model.Sections)
            section.IsExpanded = expanded;

        var window = new ActivityWindow(model);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Width = 560;
        window.Height = 560;
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Button _Button(ActivityWindow window, string name) =>
        window.FindControl<Button>(name) ?? throw new InvalidOperationException($"{name} was not rendered");

    /// <summary>Every colour the frame actually contains. Exact matches only: an accent that is on screen is on
    /// screen at full strength somewhere, and a softened or antialiased near-miss proves nothing either way.</summary>
    private static HashSet<Color> _Colours(Bitmap frame)
    {
        var area = new PixelRect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height);
        var pixels = new byte[area.Width * area.Height * 4];
        frame.CopyPixels(area, Marshal.UnsafeAddrOfPinnedArrayElement(pixels, 0), pixels.Length, area.Width * 4);

        // The headless Skia backend hands these over as Rgba8888, not the Bgra8888 the rest of the world assumes.
        // Read the channels the wrong way round and every colour comes out as a plausible-looking different colour,
        // which is the one failure mode a colour assertion cannot survive.
        bool rgba = frame.Format == PixelFormat.Rgba8888;

        var colours = new HashSet<Color>();
        for (var i = 0; i < pixels.Length; i += 4)
            colours.Add(rgba
                ? Color.FromArgb(pixels[i + 3], pixels[i], pixels[i + 1], pixels[i + 2])
                : Color.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]));

        return colours;
    }

    /// <summary>Every piece of text this view model puts on screen. Gathered by reflection rather than from a
    /// hand-kept list, so a label added later cannot slip past the rule above it.</summary>
    private static IEnumerable<string> _ExposedText(ActivityWindowViewModel model) =>
        typeof(ActivityWindowViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0)
            .Select(property => property.GetValue(model) as string)
            .OfType<string>()
            .Concat(model.Sections.Select(section => section.Title))
            .Concat(model.Sections.Select(section => section.HeaderSummary))
            .Concat(model.Activity().WeatherChoices.Select(choice => choice.Tooltip).OfType<string>());

    /// <summary>Every resource key the markup asks for under one of the two markup extensions.</summary>
    private static IEnumerable<string> _Keys(string markup, string extension) =>
        Regex.Matches(markup, @"\{" + extension + @"\s+([A-Za-z0-9_]+)\s*\}")
            .Select(match => match.Groups[1].Value)
            .Distinct();

    /// <summary>The keys that actually change with the faction — the ones every one of the four palettes defines.
    /// Read from the palettes rather than listed here, so a key added to them is covered without a second edit.</summary>
    private static HashSet<string> _FactionKeys()
    {
        List<HashSet<string>> perFaction = Enum.GetNames<FactionTheme>()
            .Select(faction => _SourcePath($"EveUtils.Client/Themes/Factions/{faction}.axaml"))
            .Select(path => Regex.Matches(File.ReadAllText(path), @"x:Key=""([A-Za-z0-9_]+)""")
                .Select(match => match.Groups[1].Value)
                .ToHashSet())
            .ToList();

        var shared = perFaction[0];
        foreach (var keys in perFaction.Skip(1))
            shared.IntersectWith(keys);

        return shared;
    }

    /// <summary>The repository file, found from the test binary rather than from a checkout path baked in here.</summary>
    private static string _SourcePath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EVE-Together.slnx")))
            directory = directory.Parent;

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("the solution root is not above the test binary"),
            relative);
    }
}
