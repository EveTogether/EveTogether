using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.Views;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
// Both halves of the run live here at once, and each name is taken twice: the window's own ActivityKind beside the
// store's, and Avalonia's Dispatcher beside the CQRS one.
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;
using Result = EveUtils.Shared.Messaging.Result;
using StoredActivityKind = EveUtils.Shared.Modules.Runs.Enums.ActivityKind;
using StoredRunState = EveUtils.Shared.Modules.Runs.Enums.RunState;

namespace EveUtils.Client.UiTests;

/// <summary>
/// What Raymond found flying with the window open (ET-98, after the four phases): the FLEET section counted members
/// without naming them, a stopped run's start and end could not be corrected before saving, and a successful save
/// left an empty overlay lying over the game.
/// </summary>
public sealed class ActivityWindowCorrectionTests
{
    private static readonly DateTime Anchor = new(2026, 9, 1, 20, 0, 0, DateTimeKind.Utc);

    // ── AC-1 — the fleet section names who it counted, and says what it counted ─────────────────────

    [Fact]
    public void TheFleetSection_NamesEveryMemberItHeardFrom_AndSaysWhatTheCountIsMadeOf()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Site, _Unused());

        model.ApplyFleetEnvelope(
        [
            new MetricSample(1, 7, MetricKind.Location, 0, 1_000_000, Text: "Bhizheba"),
            new MetricSample(2, 7, MetricKind.Location, 0, 1_000_000, Text: "Amarr")
        ], Anchor);

        Assert.Equal(2, model.FleetMembers.Count);
        Assert.Equal(["Char 1", "Char 2"], model.FleetMembers.Select(member => member.Name));
        Assert.Equal(["Bhizheba", "Amarr"], model.FleetMembers.Select(member => member.LocationText));

        // The count and the list are the same fact, and the line beside them says which fact that is.
        Assert.Equal(model.FleetMembers.Count, model.FleetMemberCount);
        Assert.Contains("sharing their location", model.FleetStatusText, StringComparison.Ordinal);
        Assert.Contains("not sharing theirs", model.FleetBasisText, StringComparison.Ordinal);
        Assert.True(model.IsFleetShown);
    }

    /// <summary>
    /// The counterproof AC-1 asks for. <c>FleetMemberCount</c> counts location samples, so a third member who does
    /// not share a location is in the fleet and in neither the number nor the list. What this holds is that the
    /// difference is on screen rather than silent: the member is visibly absent from a list captioned with what it
    /// is a list of, instead of being quietly folded into a total that claims to be the fleet.
    /// </summary>
    [Fact]
    public void AMemberWhoSharesNoLocation_IsVisiblyAbsent_NotSilentlyFoldedIntoTheTotal()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Site, _Unused());
        MetricSample first = new(1, 7, MetricKind.Location, 0, 1_000_000, Text: "Bhizheba");
        MetricSample second = new(2, 7, MetricKind.Location, 0, 1_000_000, Text: "Amarr");
        // The third member is in the fleet and shares no location: nothing about them reaches this window.
        MetricSample thirdsDps = new(3, 7, MetricKind.Dps, 412, 1_000_000);

        model.ApplyFleetEnvelope([first, second, thirdsDps], Anchor);
        string withoutThird = model.FleetStatusText;

        Assert.Equal(2, model.FleetMemberCount);
        Assert.DoesNotContain(model.FleetMembers, member => member.CharacterId == 3);

        // And once they do share one, both the list and the count move — the same visible change, the other way.
        model.ApplyFleetEnvelope([first, second, new MetricSample(3, 7, MetricKind.Location, 0, 1_000_000, Text: "Jita")],
            Anchor);

        Assert.Equal(3, model.FleetMemberCount);
        Assert.NotEqual(withoutThird, model.FleetStatusText);
        Assert.Contains(model.FleetMembers, member => member is { CharacterId: 3, LocationText: "Jita" });
    }

    [Fact]
    public void AMemberWhoStopsSharing_LeavesTheList_RatherThanStandingOnAStaleSample()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Site, _Unused());
        MetricSample first = new(1, 7, MetricKind.Location, 0, 1_000_000, Text: "Bhizheba");

        model.ApplyFleetEnvelope([first, new MetricSample(2, 7, MetricKind.Location, 0, 1_000_000, Text: "Amarr")],
            Anchor);
        model.ApplyFleetEnvelope([first], Anchor.AddSeconds(1));

        Assert.Equal(1, model.FleetMemberCount);
        Assert.Equal([1], model.FleetMembers.Select(member => member.CharacterId));
    }

    // ── AC-2 — start and end are correctable on a stopped run, before saving ────────────────────────

    [Fact]
    public void CorrectingTheTimes_MovesTheClockAndTheFigures_AndSaysTheyWereCorrected()
    {
        var model = _Stopped(ActivityKind.Site);

        Assert.True(model.IsTimeCorrectionShown);
        Assert.False(model.IsTimeCorrected);
        Assert.Equal("06:00", model.ClockText);
        Assert.Equal("measured from START and STOP", model.TimeSourceText);

        // You pressed START half a minute late and STOP a minute late: the run was longer than the clock says.
        model.StartCorrectionText = _Local(Anchor.AddSeconds(-30));
        model.EndCorrectionText = _Local(Anchor.AddMinutes(7));
        model.ApplyTimeCorrectionCommand.Execute(null);

        Assert.Null(model.TimeCorrectionError);
        Assert.True(model.IsTimeCorrected);
        Assert.Equal("07:30", model.ClockText);
        Assert.Equal(_Local(Anchor.AddSeconds(-30)), model.StartText);
        Assert.Equal(_Local(Anchor.AddMinutes(7)), model.EndText);
        Assert.Contains("corrected by hand", model.TimeSourceText, StringComparison.Ordinal);
    }

    /// <summary>The counterproof AC-2 asks for: an end before its start is refused with the reason, and nothing
    /// about the run moves — not the clock, not the figures, and not the corrected/measured verdict.</summary>
    [Fact]
    public void AnEndBeforeItsStart_IsRefusedWithTheReason_AndNothingIsQuietlyStraightenedOut()
    {
        var model = _Stopped(ActivityKind.Site);

        model.StartCorrectionText = _Local(Anchor.AddMinutes(5));
        model.EndCorrectionText = _Local(Anchor.AddMinutes(2));
        model.ApplyTimeCorrectionCommand.Execute(null);

        Assert.Equal("The end cannot be before the start.", model.TimeCorrectionError);
        Assert.True(model.HasTimeCorrectionError);
        Assert.False(model.IsTimeCorrected);
        Assert.Null(model.CorrectedStartUtc);
        Assert.Null(model.CorrectedStopUtc);
        Assert.Equal("06:00", model.ClockText);
        Assert.Equal(_Local(Anchor), model.StartText);
    }

    /// <summary>The second counterproof AC-2 asks for. Twenty minutes is the whole of an abyssal run —
    /// <c>AbyssalSpace.RunLimit</c>, past which the ship and the pod are gone — so a typed duration over it is
    /// provably wrong and is free to catch.</summary>
    [Fact]
    public void AnAbyssalRunCorrectedPastTheRunLimit_IsRefused()
    {
        var model = _Stopped(ActivityKind.Abyssal);

        model.StartCorrectionText = _Local(Anchor);
        model.EndCorrectionText = _Local(Anchor + AbyssalSpace.RunLimit + TimeSpan.FromSeconds(1));
        model.ApplyTimeCorrectionCommand.Execute(null);

        Assert.NotNull(model.TimeCorrectionError);
        Assert.Contains("20 minutes", model.TimeCorrectionError, StringComparison.Ordinal);
        Assert.False(model.IsTimeCorrected);

        // Exactly the limit is a run that ended at the deadline, which happens and is not an error.
        model.EndCorrectionText = _Local(Anchor + AbyssalSpace.RunLimit);
        model.ApplyTimeCorrectionCommand.Execute(null);

        Assert.Null(model.TimeCorrectionError);
        Assert.True(model.IsTimeCorrected);
    }

    /// <summary>
    /// The design answer this ticket asked for, held in code: a member's own correction moves their own row and
    /// leaves the group's envelope where the fleet's samples put it. <see cref="ActivityWindowViewModel.AnchorUtc"/>
    /// <i>is</i> that envelope — the earliest anchor over the whole fleet — so what this holds is that correcting
    /// your clock never re-anchors anybody else's.
    /// </summary>
    [Fact]
    public void CorrectingYourOwnTime_LeavesTheGroupEnvelopeWhereTheFleetPutIt()
    {
        var model = new ActivityWindowViewModel(ActivityKind.Abyssal, _Unused());
        // Someone else entered first, so the envelope hangs on their anchor and not on this pilot's.
        model.ApplyFleetEnvelope(
        [
            new MetricSample(1, 7, MetricKind.Location, 0, 1_000_000, AbyssalAnchorMs: 700_000),
            new MetricSample(2, 7, MetricKind.Location, 0, 1_000_000, AbyssalAnchorMs: 900_000)
        ], Anchor);
        DateTime envelope = Assert.IsType<DateTime>(model.AnchorUtc);
        model.StopRun(envelope.AddMinutes(6));

        model.StartCorrectionText = _Local(envelope.AddMinutes(2));
        model.EndCorrectionText = _Local(envelope.AddMinutes(6));
        model.ApplyTimeCorrectionCommand.Execute(null);

        Assert.Null(model.TimeCorrectionError);
        Assert.Equal(envelope, model.AnchorUtc);
        Assert.Equal(envelope.AddMinutes(2), model.EffectiveStartUtc);
        Assert.Contains("moves this run only", model.TimeSourceText, StringComparison.Ordinal);
    }

    /// <summary>What is corrected is what is stored — the point of the whole exercise. Through the real store, so
    /// the row that comes back is the row a later report reads.</summary>
    [AvaloniaFact]
    public async Task WhatSaveStores_IsTheCorrectedTime_NotTheMeasuredOne()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ActivityWindowViewModel model = await harness.OpenAsync();

        await model.StartRunCommand.ExecuteAsync(null);
        Assert.NotNull(model.RunId);
        DateTime measuredStart = Assert.IsType<DateTime>(model.AnchorUtc);
        model.StopRun(measuredStart.AddMinutes(6));

        model.StartCorrectionText = _Local(measuredStart.AddSeconds(-45));
        model.EndCorrectionText = _Local(measuredStart.AddMinutes(8));
        model.ApplyTimeCorrectionCommand.Execute(null);
        Assert.Null(model.TimeCorrectionError);

        await model.SaveRunCommand.ExecuteAsync(null);
        Assert.Equal(ActivityRunState.Saved, model.RunState);

        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Run stored = Assert.Single(await db.Set<Run>().ToListAsync(cancellationToken));
        Assert.Equal(StoredRunState.Saved, stored.State);
        Assert.Equal(_Second(measuredStart.AddSeconds(-45)), _Second(stored.StartedAtUtc));
        Assert.Equal(_Second(measuredStart.AddMinutes(8)), _Second(Assert.IsType<DateTime>(stored.StoppedAtUtc)));
        // The corrected moments are written over the measured ones, so this stamp is all that is left to say the
        // duration was typed rather than clocked.
        Assert.NotNull(stored.TimesCorrectedAtUtc);
    }

    /// <summary>
    /// The distinction has to survive the save, or it is not a distinction. A corrected run and an uncorrected one
    /// are told apart by the stored row alone — no window open, no memory of the session that flew it. Written
    /// against two runs at once because "not null" on its own proves nothing about the run that was never touched.
    /// </summary>
    [AvaloniaFact]
    public async Task ASavedRun_SaysWhetherItsTimesWereCorrected_OrMeasured()
    {
        using var instance = TestClientInstance.Create();
        CqrsDispatcher dispatcher = instance.Services.GetRequiredService<CqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Guid measured = (await dispatcher.Send(
            new StartRunCommand(90000001, StoredActivityKind.Site, Anchor, 1234, "Homefront", 30000142),
            cancellationToken)).Value;
        Assert.True((await dispatcher.Send(new SaveRunCommand(measured, Anchor.AddMinutes(6), Anchor.AddMinutes(6),
            [], [], [], []), cancellationToken)).IsSuccess);

        Guid corrected = (await dispatcher.Send(
            new StartRunCommand(90000002, StoredActivityKind.Site, Anchor, 1234, "Homefront", 30000142),
            cancellationToken)).Value;
        Assert.True((await dispatcher.Send(new SaveRunCommand(corrected, Anchor.AddMinutes(8), Anchor.AddMinutes(8),
            [], [], [], [], StartedAtUtc: Anchor.AddSeconds(-45),
            TimesCorrectedAtUtc: Anchor.AddMinutes(8)), cancellationToken)).IsSuccess);

        await using ClientDbContext db = await instance.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        Assert.Null((await db.Set<Run>().SingleAsync(run => run.Id == measured, cancellationToken))
            .TimesCorrectedAtUtc);
        Run typed = await db.Set<Run>().SingleAsync(run => run.Id == corrected, cancellationToken);
        Assert.Equal(Anchor.AddMinutes(8), typed.TimesCorrectedAtUtc);
        Assert.Equal(Anchor.AddSeconds(-45), typed.StartedAtUtc);
    }

    /// <summary>The store is the last place a corrected time passes through, so it checks the pair itself rather
    /// than trusting whichever screen sent it.</summary>
    [AvaloniaFact]
    public async Task TheStoreItself_RefusesARunThatEndsBeforeItStarted()
    {
        using var instance = TestClientInstance.Create();
        CqrsDispatcher dispatcher = instance.Services.GetRequiredService<CqrsDispatcher>();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        EveUtils.Shared.Messaging.Result<Guid> started = await dispatcher.Send(
            new StartRunCommand(90000001, StoredActivityKind.Site, Anchor, 1234, "Homefront", 30000142), cancellationToken);
        Result saved = await dispatcher.Send(new SaveRunCommand(started.Value, Anchor.AddMinutes(-1),
            Anchor.AddMinutes(1), [], [], [], []), cancellationToken);

        Assert.False(saved.IsSuccess);
        Assert.Contains(saved.Messages, message => message.Text == "A run cannot end before it started.");
    }

    // ── AC-3 / AC-4 — SAVE closes this window, and only on a save that landed ───────────────────────

    [AvaloniaFact]
    public async Task ASuccessfulSave_ClosesTheWindow()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        model.StopRun(DateTime.UtcNow);

        var window = new ActivityWindow(model);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);

        await model.SaveRunCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ActivityRunState.Saved, model.RunState);
        Assert.False(window.IsVisible);
        Assert.Null(model.SaveFailureText);
    }

    /// <summary>The counterproof AC-3 asks for: make the save fail and the window is still standing, with the
    /// reason on it. A window that closed here would take the run with it and nobody would know.</summary>
    [AvaloniaFact]
    public async Task AFailedSave_LeavesTheWindowStanding_WithTheReasonOnIt()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        model.StopRun(DateTime.UtcNow);
        // A real refusal from the store, reached the way it happens: the row was already committed elsewhere while
        // this window still had it open, and the second save is the one the handler turns down.
        Guid runId = Assert.IsType<Guid>(model.RunId);
        Result first = await harness.Services.GetRequiredService<CqrsDispatcher>()
            .Send(new SaveRunCommand(runId, DateTime.UtcNow, DateTime.UtcNow, [], [], [], []));
        Assert.True(first.IsSuccess);

        var window = new ActivityWindow(model);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await model.SaveRunCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(ActivityRunState.Saved, model.RunState);
        Assert.True(window.IsVisible);
        Assert.True(model.HasSaveFailure);
        Assert.False(string.IsNullOrWhiteSpace(model.SaveFailureText));

        TextBlock reason = window.FindControl<TextBlock>("SaveFailureText")
            ?? throw new InvalidOperationException("SaveFailureText was not rendered");
        Assert.Equal(model.SaveFailureText, reason.Text);
        window.Close();
    }

    /// <summary>AC-4. Saving is each member's own; the close is raised by the view model this window owns and
    /// reaches nothing else. A second window on a second member's run is untouched by the first one's save.</summary>
    [AvaloniaFact]
    public async Task InAGroupRun_ASaveClosesOnlyItsOwnWindow()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel mine = await harness.OpenAsync();
        await mine.StartRunCommand.ExecuteAsync(null);
        mine.StopRun(DateTime.UtcNow);

        // A second member's window, on its own view model, sharing the group.
        var theirs = new ActivityWindowViewModel(ActivityKind.Site, harness.Services) { GroupCode = "ABCDE" };
        mine.GroupCode = "ABCDE";

        var myWindow = new ActivityWindow(mine);
        var theirWindow = new ActivityWindow(theirs);
        myWindow.Show();
        theirWindow.Show();
        Dispatcher.UIThread.RunJobs();

        await mine.SaveRunCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(myWindow.IsVisible);
        Assert.True(theirWindow.IsVisible);
        Assert.NotEqual(ActivityRunState.Saved, theirs.RunState);
        theirWindow.Close();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static ActivityWindowViewModel _Stopped(ActivityKind kind)
    {
        var model = new ActivityWindowViewModel(kind, _Unused());
        model.StartManualRun(Anchor);
        model.StopRun(Anchor.AddMinutes(6));
        return model;
    }

    private static string _Local(DateTime utc) =>
        utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Second precision: the corrections are typed as HH:mm:ss, so that is all a stored time can carry
    /// back.</summary>
    private static DateTime _Second(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, utc.Kind);

    private static IServiceProvider _Unused() => new ServiceCollection().BuildServiceProvider();
}
