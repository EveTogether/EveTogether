using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Runs.Dtos;
using StoredActivityKind = EveUtils.Shared.Modules.Runs.Enums.ActivityKind;
using LootCaptureSource = EveUtils.Shared.Modules.Runs.Enums.LootCaptureSource;
using LootKind = EveUtils.Shared.Modules.Runs.Enums.LootKind;
using EnemyObservationDirection = EveUtils.Shared.Modules.Runs.Enums.EnemyObservationDirection;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using RunCommands = EveUtils.Shared.Modules.Runs.Commands;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The seams between the activity window and the application, each proved from the source the application actually
/// uses: a line EVE wrote in a gamelog file, a fleet sample on the bus, the run row in the database. None of these
/// tests calls a method on the window to fill a section — take the wiring out and they go red, which is the only
/// thing that distinguishes a connected window from one whose tests do its work for it.
/// </summary>
public class ActivityWindowWiringTests
{
    // ── START is a run, not a stopwatch ─────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task Start_CreatesTheRunTheLootIsFiledUnder()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        // A combat anomaly, exactly as ClipboardSignatureOffer hands one over.
        model.SignatureGroup = "Combat Site";
        model.SignatureName = "Sansha Hideaway";

        await model.StartRunCommand.ExecuteAsync(null);

        var dispatcher = harness.Services.GetRequiredService<IDispatcher>();
        Result<RunningRunDto> running = await dispatcher.Query(new GetRunningRunQuery());
        Assert.True(running.IsSuccess, "START left no running run in the store");
        Assert.Equal(model.RunId, running.Value!.Id);
        Assert.Equal("Sansha Hideaway", running.Value.SiteName);
        Assert.Equal(StoredActivityKind.Site, running.Value.ActivityKind);
        Assert.Equal(ActivityWindowHarness.CharacterId, running.Value.CharacterId);

        // And the LOOT section is on the same run: the clipboard's own command finds it, and the window shows it.
        Result<RunLootCaptureSaveResult> stored = await dispatcher.Send(
            new RunCommands.AddRunLootCaptureCommand(_Capture()));
        Assert.True(stored.IsSuccess, "the loot capture found no running run to attach to");
        await model.RunLoot!.RefreshAsync();
        Assert.Null(model.RunLoot.RunStatusMessage);
        Assert.Single(model.RunLoot.Captures);
    }

    /// <summary>Raymond's evening in one test: the clock says a run is on and the LOOT section says none is.</summary>
    [AvaloniaFact]
    public async Task WhileTheClockRuns_TheLootSectionNeverSaysNoRunIsRunning()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();

        await model.StartRunCommand.ExecuteAsync(null);
        model.Refresh(DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(ActivityRunState.Running, model.RunState);
        Assert.DoesNotContain("No run is running", model.Loot.HeaderSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Null(model.RunLoot!.RunStatusMessage);
    }

    [AvaloniaFact]
    public async Task Stop_KeepsTheRunOpen_SoLootCopiedAfterTheFightStillLands()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.StartRunCommand.ExecuteAsync(null);

        model.StopRun(DateTime.UtcNow);

        Result<RunLootCaptureSaveResult> stored = await harness.Services.GetRequiredService<IDispatcher>()
            .Send(new RunCommands.AddRunLootCaptureCommand(_Capture()));
        Assert.True(stored.IsSuccess, "stopping the clock closed the run the loot belongs to");
        Assert.True(model.IsSaveButtonVisible, "there is nowhere to save the run he just stopped");
    }

    [AvaloniaFact]
    public async Task Save_CommitsTheRun_AndLeavesNothingRunning()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        model.StopRun(DateTime.UtcNow);

        await model.SaveRunCommand.ExecuteAsync(null);

        Assert.Equal(ActivityRunState.Saved, model.RunState);
        Assert.False((await harness.Services.GetRequiredService<IDispatcher>()
            .Query(new GetRunningRunQuery())).IsSuccess);
        Assert.False(model.IsSaveButtonVisible);
        Assert.False(model.IsDiscardButtonVisible);
        Assert.True(model.IsStartButtonVisible);
    }

    [AvaloniaFact]
    public async Task Discard_EndsTheRun_AndTheWindowStopsClaimingOne()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        harness.Dialogs.OnConfirm = (_, _) => Task.FromResult(true);

        await model.DiscardRunCommand.ExecuteAsync(null);

        Assert.Equal(ActivityRunState.NotStarted, model.RunState);
        Assert.Null(model.RunId);
        Assert.False((await harness.Services.GetRequiredService<IDispatcher>()
            .Query(new GetRunningRunQuery())).IsSuccess);
    }

    /// <summary>Closing the window does not end the run, so opening one again has to find it rather than start a
    /// second — two running rows is the state that breaks every loot copy afterwards.</summary>
    [AvaloniaFact]
    public async Task ReopeningTheWindow_AttachesToTheRunAlreadyRunning()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel first = await harness.OpenAsync();
        await first.StartRunCommand.ExecuteAsync(null);
        Guid? started = first.RunId;
        first.Dispose();

        ActivityWindowViewModel reopened = await harness.OpenAsync();

        Assert.Equal(started, reopened.RunId);
        Assert.Equal(ActivityRunState.Running, reopened.RunState);
        Result<RunningRunDto> running = await harness.Services.GetRequiredService<IDispatcher>()
            .Query(new GetRunningRunQuery());
        Assert.True(running.IsSuccess, "reopening started a second run beside the first");
        Assert.Equal(running.Value!.StartedAtUtc, reopened.AnchorUtc);
    }

    // ── The gamelog reaches the window ──────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task ABountyLineInTheGamelog_ReachesTheBountySection()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await harness.StartWatchingAsync();
        await model.StartRunCommand.ExecuteAsync(null);

        await harness.WriteLineAsync(ActivityWindowHarness.BountyLine("67.500"));

        await ActivityWindowHarness.WaitUntil(() => model.BountyIsk > 0);
        Assert.Equal(67_500, model.BountyIsk);
        Assert.Contains("67,500", model.BountyText);
        Assert.Contains("67,500", model.Bounty.HeaderSummary);
    }

    /// <summary>The payouts are the run's own rows, at the times the log wrote them — SAVE hands them to the store
    /// rather than a single total the window added up on screen.</summary>
    [AvaloniaFact]
    public async Task TheBountiesOfARun_AreSavedWithIt()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await harness.StartWatchingAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        await harness.WriteLineAsync(ActivityWindowHarness.BountyLine("4875"));
        await ActivityWindowHarness.WaitUntil(() => model.BountyIsk > 0);

        model.StopRun(DateTime.UtcNow);
        await model.SaveRunCommand.ExecuteAsync(null);

        Assert.Equal(ActivityRunState.Saved, model.RunState);
        Assert.Equal(4875, model.BountyIsk);
    }

    [AvaloniaFact]
    public async Task AJumpLineInTheGamelog_ReachesTheLocationRow()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await harness.StartWatchingAsync();

        await harness.WriteLineAsync(ActivityWindowHarness.JumpLine("Aphend"));

        await ActivityWindowHarness.WaitUntil(() =>
        {
            model.Refresh(DateTime.UtcNow);
            return model.LocationText == "Aphend";
        });
        Assert.Equal("Aphend", model.LocationText);
        Assert.True(model.IsLocationShown);
    }

    /// <summary>The same reading, for a pilot the client knows is not in the game: the system they undocked in is
    /// not where they are, and the window says so rather than showing a stale one (ET-71).</summary>
    [AvaloniaFact]
    public async Task ALoggedOffPilot_ReadsAsOffline_NotAsTheSystemTheyLeft()
    {
        using var harness = await ActivityWindowHarness.CreateAsync(inGame: false);
        ActivityWindowViewModel model = await harness.OpenAsync();
        await harness.StartWatchingAsync();

        await harness.WriteLineAsync(ActivityWindowHarness.JumpLine("Aphend"));

        await ActivityWindowHarness.WaitUntil(() =>
        {
            model.Refresh(DateTime.UtcNow);
            return model.LocationText == "offline";
        });
        Assert.Equal("offline", model.LocationText);
    }

    /// <summary>
    /// A combat line, through the same pump the bounty took, into the enemy list the run is saved with. The line's
    /// own time travels with it: EVE flushes its log in chunks, so a handler that stamped "now" would file a whole
    /// batch at one instant (ET-105's storage seam).
    /// </summary>
    [AvaloniaFact]
    public async Task ACombatLineInTheGamelog_ReachesTheEnemyList_AtItsOwnTime()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await harness.StartWatchingAsync();
        await model.StartRunCommand.ExecuteAsync(null);

        await harness.WriteLineAsync(ActivityWindowHarness.CombatLine(250, "Centii Servant"));

        await ActivityWindowHarness.WaitUntil(() => model.EnemyObservations.Count > 0);
        RunEnemyObservationViewModel observed = Assert.Single(model.EnemyObservations);
        Assert.Equal("Centii Servant", observed.EnemyName);
        Assert.Equal(new DateTime(2030, 1, 1, 12, 0, 5, DateTimeKind.Utc), observed.FirstObservedAtUtc);
        Assert.Equal(EnemyObservationDirection.To, observed.Direction);
    }

    // ── The fleet reaches the window ────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task WithNoFleetSample_TheFleetSectionIsNotOnScreen_AndNothingClaimsSolo()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();

        Assert.False(model.IsFleetShown);
        Assert.DoesNotContain("solo", model.Fleet.HeaderSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("solo", model.FleetStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task AFleetMembersSampleOnTheBus_FillsTheFleetSection()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Abyssal);
        var bus = harness.Services.GetRequiredService<IEventBus>();
        long sentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (int characterId in new[] { ActivityWindowHarness.CharacterId, 90000002 })
            await bus.PublishAsync(new FleetMetricEvent(new MetricSample(
                characterId, 42, MetricKind.Location, 0, sentMs, "Abyssal",
                AbyssalAnchorMs: sentMs - 60_000), characterId));

        await ActivityWindowHarness.WaitUntil(() => model.FleetMemberCount == 2);
        Assert.True(model.IsFleetShown);
        Assert.Equal(2, model.FleetMemberCount);
        Assert.Equal(2, model.AnchoredFleetMemberCount);
        Assert.Equal("based on 2 of 2 members", model.Fleet.HeaderSummary);
    }

    /// <summary>A fleet run nobody pressed START for still gets its row: the envelope is what began it, and the loot
    /// has to have somewhere to go all the same.</summary>
    [AvaloniaFact]
    public async Task AnAbyssalRunStartedByTheFleetEnvelope_AlsoGetsItsStoredRun()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Abyssal);
        var bus = harness.Services.GetRequiredService<IEventBus>();
        long sentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await bus.PublishAsync(new FleetMetricEvent(new MetricSample(
            ActivityWindowHarness.CharacterId, 42, MetricKind.Location, 0, sentMs, "Abyssal",
            AbyssalAnchorMs: sentMs - 60_000), ActivityWindowHarness.CharacterId));

        await ActivityWindowHarness.WaitUntil(() => model.RunId is not null);
        Assert.Equal(ActivityRunState.Running, model.RunState);
        Result<RunningRunDto> running = await harness.Services.GetRequiredService<IDispatcher>()
            .Query(new GetRunningRunQuery());
        Assert.True(running.IsSuccess, "the fleet's envelope started a clock but no run");
        Assert.Equal(StoredActivityKind.Abyssal, running.Value!.ActivityKind);
    }

    // ── The four buttons against the four states ────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task TheRunControls_MatchTheStateOfTheRun()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();

        _AssertButtons(model, "not started", start: true, stop: false, save: false, discard: false);

        await model.StartRunCommand.ExecuteAsync(null);
        _AssertButtons(model, "running", start: false, stop: true, save: false, discard: true);

        model.StopRun(DateTime.UtcNow);
        _AssertButtons(model, "stopped", start: true, stop: false, save: true, discard: true);

        await model.SaveRunCommand.ExecuteAsync(null);
        _AssertButtons(model, "saved", start: true, stop: false, save: false, discard: false);
    }

    private static void _AssertButtons(ActivityWindowViewModel model, string state,
        bool start, bool stop, bool save, bool discard)
    {
        Assert.Equal((state, start), (state, model.IsStartButtonVisible));
        Assert.Equal((state, stop), (state, model.IsStopButtonVisible));
        Assert.Equal((state, save), (state, model.IsSaveButtonVisible));
        Assert.Equal((state, discard), (state, model.IsDiscardButtonVisible));
    }

    private static RunLootCaptureInput _Capture() => new()
    {
        CapturedAtUtc = DateTime.UtcNow,
        Source = LootCaptureSource.Clipboard,
        ContentHash = "hash-" + Guid.NewGuid().ToString("N"),
        Entries =
        [
            new RunLootEntryInput
            {
                ItemTypeId = 34,
                Name = "Tritanium",
                Quantity = 100,
                Volume = 1,
                ClipboardPrice = 5,
                LootKind = LootKind.Gained
            }
        ]
    };
}
