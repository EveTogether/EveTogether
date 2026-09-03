using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.Platform;
using EveUtils.Client.Views;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using StoredActivityKind = EveUtils.Shared.Modules.Runs.Enums.ActivityKind;
using LootCaptureSource = EveUtils.Shared.Modules.Runs.Enums.LootCaptureSource;
using LootKind = EveUtils.Shared.Modules.Runs.Enums.LootKind;
using StoredRunState = EveUtils.Shared.Modules.Runs.Enums.RunState;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        model.SignatureId = "RUS-326";
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

    /// <summary>
    /// A copy taken while the window stands open reaches the section that shows it. Raymond, 2026-09-02: the toast
    /// said "Loot copied" and the LOOT section under it went on reading "no loot captured" — the capture really was
    /// filed against the run, and the window simply never looked again. Stored through the same command the
    /// clipboard uses, with nothing calling the window afterwards, so it is the wiring that is being measured.
    /// </summary>
    [AvaloniaFact]
    public async Task LootCopiedWhileTheWindowIsOpen_ReachesTheSectionThatShowsIt()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        Assert.Empty(model.RunLoot!.Captures);

        await harness.Services.GetRequiredService<IDispatcher>()
            .Send(new RunCommands.AddRunLootCaptureCommand(_Capture()));

        await ActivityWindowHarness.WaitUntil(() => model.RunLoot.Captures.Count > 0);
        Assert.Single(model.RunLoot.Captures);
        Assert.DoesNotContain("no loot captured", model.Loot.HeaderSummary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A signature copied while a run is going reaches the window that is up. Raymond copied Drone Cluster, pressed
    /// "start run", and got the Sansha Hideaway run he was already in, still ticking, three times over: the offer
    /// builds a fresh view model and DialogService dropped it because a window already existed.
    ///
    /// A different site does not take the run with it. The clock stops, the copied site waits, and DISCARD is what
    /// hands the window over — nothing is saved or thrown away for him.
    /// </summary>
    [AvaloniaFact]
    public async Task ASignatureCopiedOnARunningRun_ClosesThatRunOut_AndTakesTheNewSite()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        model.SignatureId = "RUS-326";
        model.SignatureName = "Sansha Hideaway";
        await model.StartRunCommand.ExecuteAsync(null);
        Guid? open = model.RunId;
        Assert.Equal(ActivityRunState.Running, model.RunState);

        await model.ApplySignatureAsync("SUG-270", "Combat Site", "Drone Cluster", []);

        Assert.Equal("Drone Cluster", model.SignatureName);
        Assert.Equal(ActivityRunState.NotStarted, model.RunState);
        Assert.Null(model.RunId);
        Assert.True(model.IsStartButtonVisible);

        Run left = await _RunAsync(harness, open!.Value);
        Assert.Equal(StoredRunState.Stopped, left.State);
        Assert.Null(left.DeletedAtUtc);
    }

    /// <summary>
    /// The diagnosis line names the run it closed, not the one that replaced it. It read SignatureName after
    /// _SetSignature had already moved that on, so it reported the site just copied as the site just ended — in the
    /// one line whose whole job is telling us where to look.
    /// </summary>
    [AvaloniaFact]
    public async Task TheDecisionLine_NamesTheRunThatWasClosed_NotTheOneThatReplacedIt()
    {
        var log = new RecordingLoggerProvider();
        using var harness = await ActivityWindowHarness.CreateAsync(
            configure: services => services.AddLogging(builder => builder.AddProvider(log)));
        ActivityWindowViewModel model = await harness.OpenAsync();
        model.SignatureId = "RUS-326";
        model.SignatureName = "Sansha Hideaway";
        await model.StartRunCommand.ExecuteAsync(null);

        await model.ApplySignatureAsync("SUG-270", "Combat Site", "Drone Cluster", []);

        string line = Assert.Single(log.Messages, message => message.Contains("closed out", StringComparison.Ordinal));
        Assert.Contains("Sansha Hideaway", line, StringComparison.Ordinal);
        Assert.DoesNotContain("closed out the open Drone Cluster", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// Raymond runs Sansha Refuge after Sansha Refuge. Comparing site NAMES made the next scan look like the run
    /// already going, so he got a seventeen-minute clock on a site he had just scanned. EVE gives every scan its own
    /// id and that is what tells them apart.
    /// </summary>
    [AvaloniaFact]
    public async Task AnotherScanOfTheSameSite_IsANewRun_NotTheOneAlreadyGoing()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        model.SignatureId = "RUS-326";
        model.SignatureName = "Sansha Refuge";
        await model.StartRunCommand.ExecuteAsync(null);
        Guid? first = model.RunId;

        await model.ApplySignatureAsync("SUG-270", "Combat Site", "Sansha Refuge", []);

        Assert.Equal("Sansha Refuge", model.SignatureName);
        Assert.Equal("SUG-270", model.SignatureId);
        Assert.Null(model.RunId);                                   // the old run is not this one
        Assert.Equal(ActivityRunState.NotStarted, model.RunState);  // and its clock is not ours either
        Assert.Equal(StoredRunState.Stopped, (await _RunAsync(harness, first!.Value)).State);
    }

    /// <summary>The other half: the same scan is the same run, and picking it up again must not end it.</summary>
    [AvaloniaFact]
    public async Task TheSameScanCopiedAgain_IsStillTheRunAlreadyGoing()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        model.SignatureId = "RUS-326";
        model.SignatureName = "Sansha Refuge";
        await model.StartRunCommand.ExecuteAsync(null);
        Guid? started = model.RunId;

        await model.ApplySignatureAsync("RUS-326", "Combat Site", "Sansha Refuge", []);

        Assert.Equal(started, model.RunId);
        Assert.Equal(ActivityRunState.Running, model.RunState);
    }

    /// <summary>
    /// Three characters registered, one EVE client open: there is nothing to choose, so nothing is asked. Raymond
    /// got "Whose run is this?" over RaymondKrah, SoldierJRNL and Catbank while only RaymondKrah was logged in.
    /// </summary>
    [AvaloniaFact]
    public async Task WithOneCharacterInGame_StartDoesNotAskWhoseRunItIs()
    {
        using var harness = await _ThreeCharacters(inGame: ActivityWindowHarness.CharacterId);
        ActivityWindowViewModel model = await harness.OpenAsync();

        await model.StartRunCommand.ExecuteAsync(null);

        Assert.Null(harness.Dialogs.LastPrompt);   // never asked
        Result<RunningRunDto> running = await harness.Services.GetRequiredService<IDispatcher>()
            .Query(new GetRunningRunQuery());
        Assert.True(running.IsSuccess);
        Assert.Equal(ActivityWindowHarness.CharacterId, running.Value!.CharacterId);
    }

    /// <summary>Two at the keyboard is a real question, and only those two are offered.</summary>
    [AvaloniaFact]
    public async Task WithTwoCharactersInGame_StartAsks_AndOffersOnlyThoseTwo()
    {
        using var harness = await _ThreeCharacters(inGame: [ActivityWindowHarness.CharacterId, 90000002]);
        ActivityWindowViewModel model = await harness.OpenAsync();
        harness.Dialogs.OnPickCharacter = (_, options) => Task.FromResult<int?>(options[1].CharacterId);

        await model.StartRunCommand.ExecuteAsync(null);

        Assert.Equal("Whose run is this?", harness.Dialogs.LastPrompt);
        Assert.Equal([ActivityWindowHarness.CharacterId, 90000002],
            harness.Dialogs.LastOptions!.Select(option => option.CharacterId));
        Result<RunningRunDto> running = await harness.Services.GetRequiredService<IDispatcher>()
            .Query(new GetRunningRunQuery());
        Assert.Equal(90000002, running.Value!.CharacterId);
    }

    /// <summary>Detection seeing nobody is not knowing, not nobody: the question still gets asked, over everyone.</summary>
    [AvaloniaFact]
    public async Task WithNobodyDetectedInGame_StartStillAsks_OverEveryRegisteredCharacter()
    {
        using var harness = await _ThreeCharacters(inGame: []);
        ActivityWindowViewModel model = await harness.OpenAsync();
        harness.Dialogs.OnPickCharacter = (_, options) => Task.FromResult<int?>(options[0].CharacterId);

        await model.StartRunCommand.ExecuteAsync(null);

        Assert.Equal("Whose run is this?", harness.Dialogs.LastPrompt);
        Assert.Equal(3, harness.Dialogs.LastOptions!.Count);
    }

    private static async Task<ActivityWindowHarness> _ThreeCharacters(params int[] inGame)
    {
        var harness = await ActivityWindowHarness.CreateAsync(
            configure: services => services.AddSingleton<ILocalCharacterPresence>(
                new ActivityWindowHarness.StubPresence(inGame: true, inGame)));
        ICharacterRegistry registry = harness.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("SoldierJRNL", 90000002));
        await registry.AddOrUpdateAsync(new Character("Catbank", 90000003));
        return harness;
    }

    /// <summary>The group case on the open-window route too — it must wait, not close out.</summary>
    [AvaloniaFact]
    public async Task ASignatureCopiedOnAGroupRun_WaitsInsteadOfEndingIt()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        model.SignatureId = "RUS-326";
        model.SignatureName = "Sansha Hideaway";
        model.GroupCode = "HF-7QK2";
        await model.StartRunCommand.ExecuteAsync(null);

        await model.ApplySignatureAsync("SUG-270", "Combat Site", "Drone Cluster", []);

        Assert.Equal(ActivityRunState.Stopped, model.RunState);
        Assert.Equal("Sansha Hideaway", model.SignatureName);
        Assert.Contains("Drone Cluster", model.ClockHint, StringComparison.Ordinal);
        Assert.False(model.IsStartButtonVisible);
    }

    /// <summary>
    /// Raymond's actual route, three times over: the window was CLOSED, so a copied signature opens a fresh one —
    /// and that one adopts the run still open on another site. It used to come up with that run's clock ticking,
    /// its start time, its loot and its site, as if that were the site just copied.
    ///
    /// Driven through the real <see cref="ActivityWindow"/>, because ApplySignature is not on this path at all.
    /// </summary>
    [AvaloniaFact]
    public async Task ANewWindowOpenedOnASignature_DoesNotPresentAnOlderRunAsThatSite()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel first = await harness.OpenAsync();
        first.SignatureId = "RUS-326";
        first.SignatureName = "Sansha Hideaway";
        await first.StartRunCommand.ExecuteAsync(null);
        Guid? open = first.RunId;
        first.Dispose();   // he closes the window; the run stays open in the store

        var reopened = new ActivityWindowViewModel(ActivityKind.Site, harness.Services) { SignatureId = "SUG-270", SignatureName = "Drone Cluster" };
        var window = new ActivityWindow(reopened);
        window.Show();
        await ActivityWindowHarness.WaitUntil(() => reopened.ClockText != "--:--" || reopened.RunId is not null,
            timeoutMs: 1500);

        Assert.Equal("Drone Cluster", reopened.SignatureName);       // the site he copied, not the one he left
        Assert.Null(reopened.RunId);
        Assert.Equal(ActivityRunState.NotStarted, reopened.RunState);
        Assert.True(reopened.IsStartButtonVisible);

        // The Sansha Hideaway run is closed out, not deleted: still there, still holding what it collected.
        Run left = await _RunAsync(harness, open!.Value);
        Assert.Equal(StoredRunState.Stopped, left.State);
        Assert.Null(left.DeletedAtUtc);
        window.Close();
    }

    /// <summary>
    /// The exception, and the reason this is not just "always close the old one": a run that belongs to a group
    /// ends on every other member's machine too, and that is the FC's button. It stays, the copied site waits.
    /// </summary>
    [AvaloniaFact]
    public async Task ACopiedSiteDoesNotEndAGroupRun_ItWaitsForTheDecision()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel first = await harness.OpenAsync();
        first.SignatureId = "RUS-326";
        first.SignatureName = "Sansha Hideaway";
        first.GroupCode = "HF-7QK2";
        await first.StartRunCommand.ExecuteAsync(null);
        Guid? open = first.RunId;
        first.Dispose();

        var reopened = new ActivityWindowViewModel(ActivityKind.Site, harness.Services) { SignatureId = "SUG-270", SignatureName = "Drone Cluster" };
        var window = new ActivityWindow(reopened);
        window.Show();
        await ActivityWindowHarness.WaitUntil(() => reopened.RunId is not null);

        Assert.Equal(open, reopened.RunId);
        Assert.Equal("Sansha Hideaway", reopened.SignatureName);
        Assert.NotEqual(ActivityRunState.Running, reopened.RunState);
        Assert.Contains("Drone Cluster", reopened.ClockHint, StringComparison.Ordinal);
        Assert.False(reopened.IsStartButtonVisible);
        window.Close();
    }

    private static async Task<Run> _RunAsync(ActivityWindowHarness harness, Guid runId)
    {
        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync();
        return await db.Set<Run>().AsNoTracking().SingleAsync(run => run.Id == runId);
    }

    /// <summary>
    /// The same thing across an application restart, which is what Raymond actually did: EVE Together closed, the
    /// run left open in the database, the app started again and a fresh signature copied. A new process brings a new
    /// container and a new view model, so nothing in memory can be carrying the answer — only the store can.
    /// </summary>
    [AvaloniaFact]
    public async Task AfterRestartingTheApplication_ACopiedSignatureDoesNotInheritTheOpenRunsSite()
    {
        var before = TestClientInstance.Create();
        before.KeepDataOnDispose = true;
        var instanceName = before.InstanceName;
        Result<Guid> started = await before.Services.GetRequiredService<IDispatcher>().Send(
            new RunCommands.StartRunCommand(90000001, StoredActivityKind.Site, DateTime.UtcNow.AddHours(-1),
                SiteTypeId: 0, SiteName: "Sansha Hideaway", SolarSystemId: null));
        Assert.True(started.IsSuccess);
        before.Dispose();   // the application closes; the run stays open

        using var restarted = TestClientInstance.Create(instanceName: instanceName);
        var model = new ActivityWindowViewModel(ActivityKind.Site, restarted.Services) { SignatureId = "SUG-270", SignatureName = "Drone Cluster" };
        var window = new ActivityWindow(model);
        window.Show();
        await ActivityWindowHarness.WaitUntil(() => model.RunId is not null);

        Assert.Equal("Drone Cluster", model.SignatureName);
        Assert.Null(model.RunId);
        Assert.Equal(ActivityRunState.NotStarted, model.RunState);
        Assert.True(model.IsStartButtonVisible);
        window.Close();
    }

    /// <summary>With no run going a copied signature is simply the window's site — no waiting, no stopping.</summary>
    [AvaloniaFact]
    public async Task ASignatureCopiedOnAnIdleWindow_BecomesItsSiteOutright()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();

        model.ApplySignature("SUG-270", "Combat Site", "Drone Cluster", []);

        Assert.Equal("Drone Cluster", model.SignatureName);
        Assert.Equal(ActivityRunState.NotStarted, model.RunState);
        Assert.DoesNotContain("waiting", model.ClockHint, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Closing the window does not end the run, so opening one again has to find it rather than start a second —
    /// two running rows is the state that breaks every loot copy afterwards.
    ///
    /// Reopened through the real <see cref="ActivityWindow"/> rather than by calling <c>LoadAsync</c>, because that
    /// call was exactly what production did not make: the window went up on a constructor's worth of state and
    /// offered a START, so the run left open the night before only appeared once that button was pressed — and
    /// appeared reading sixteen hours elapsed. Where a window is already in a run, that is what it shows.
    /// </summary>
    [AvaloniaFact]
    public async Task ReopeningTheWindow_AttachesToTheRunAlreadyRunning()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel first = await harness.OpenAsync();
        await first.StartRunCommand.ExecuteAsync(null);
        Guid? started = first.RunId;
        DateTime? startedAt = first.AnchorUtc;
        first.Dispose();

        var reopened = new ActivityWindowViewModel(ActivityKind.Site, harness.Services);
        var window = new ActivityWindow(reopened);
        window.Show();
        await ActivityWindowHarness.WaitUntil(() => reopened.RunId is not null);

        Assert.Equal(started, reopened.RunId);
        Assert.Equal(ActivityRunState.Running, reopened.RunState);
        Assert.Equal(startedAt, reopened.AnchorUtc);
        Assert.False(reopened.IsStartButtonVisible, "a run this window is already in still offered a START");
        Result<RunningRunDto> running = await harness.Services.GetRequiredService<IDispatcher>()
            .Query(new GetRunningRunQuery());
        Assert.True(running.IsSuccess, "reopening started a second run beside the first");
        Assert.Equal(running.Value!.StartedAtUtc, reopened.AnchorUtc);
        window.Close();
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
    }

    /// <summary>
    /// ET-115's first counter-proof, through the gamelog the player actually has. <c>CombatObserved</c> fires both
    /// ways — "250 to Centii Servant" and "1 from Centii Servant" — and the direction is the only thing that ever
    /// made these two rows. The question the list answers is which kind of enemy and how many, so one kind is one
    /// row, and its window has to cover both sightings rather than one of them overwriting the other.
    /// </summary>
    [AvaloniaFact]
    public async Task AnEnemyMetBothWays_IsOneRow_OverAWindowCoveringBothSightings()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await harness.StartWatchingAsync();
        await model.StartRunCommand.ExecuteAsync(null);

        await harness.WriteLineAsync(ActivityWindowHarness.CombatLine(250, "Centii Servant", "12:00:05"));
        await harness.WriteLineAsync(ActivityWindowHarness.IncomingCombatLine(7, "Centii Servant", "12:00:41"));

        await ActivityWindowHarness.WaitUntil(() => model.EnemyObservations.Count > 0
            && model.EnemyObservations[0].LastObservedAtUtc.Second == 41);

        RunEnemyObservationViewModel observed = Assert.Single(model.EnemyObservations);
        Assert.Equal("Centii Servant", observed.EnemyName);
        Assert.Equal(new DateTime(2030, 1, 1, 12, 0, 5, DateTimeKind.Utc), observed.FirstObservedAtUtc);
        Assert.Equal(new DateTime(2030, 1, 1, 12, 0, 41, DateTimeKind.Utc), observed.LastObservedAtUtc);
    }

    /// <summary>
    /// ET-115's second counter-proof. STOP used to throw the collector away, and since a count of zero is never
    /// stored (ET-106) and the player types that count after the fight, no enemy observation could ever be saved at
    /// all. The list now outlives STOP for the same reason the stored run does, and the number typed on a stopped
    /// run is the number SAVE writes.
    /// </summary>
    [AvaloniaFact]
    public async Task AnEnemySeenBeforeStop_SurvivesStop_AndACountTypedAfterwardsIsSaved()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await harness.StartWatchingAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        await harness.WriteLineAsync(ActivityWindowHarness.CombatLine(250, "Centii Servant"));
        await ActivityWindowHarness.WaitUntil(() => model.EnemyObservations.Count > 0);
        Guid runId = Assert.NotNull(model.RunId);

        model.StopRun(new DateTime(2030, 1, 1, 12, 5, 0, DateTimeKind.Utc));

        // Still there with the counter usable — this is the moment the player reaches for it.
        RunEnemyObservationViewModel observed = Assert.Single(model.EnemyObservations);
        observed.Count = 4;
        await model.SaveRunCommand.ExecuteAsync(null);

        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        RunEnemyObservation stored = Assert.Single(await db.Set<RunEnemyObservation>()
            .Where(row => row.RunId == runId)
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Centii Servant", stored.EnemyName);
        Assert.Equal(4, stored.Count);
    }

    /// <summary>
    /// ET-115's fourth counter-proof, and the other half of the one above: a kind that was seen but never counted
    /// is not written to the run, and the difference is on screen rather than only in the database — the section
    /// header names both numbers, so a folded section still says what SAVE will and will not keep.
    /// </summary>
    [AvaloniaFact]
    public async Task AnEnemyLeftAtZero_IsNotStored_AndTheSectionSaysSoBeforeSave()
    {
        using var harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel model = await harness.OpenAsync();
        await harness.StartWatchingAsync();
        await model.StartRunCommand.ExecuteAsync(null);
        await harness.WriteLineAsync(ActivityWindowHarness.CombatLine(250, "Centii Servant"));
        await ActivityWindowHarness.WaitUntil(() => model.EnemyObservations.Count > 0);
        Guid runId = Assert.NotNull(model.RunId);

        model.StopRun(new DateTime(2030, 1, 1, 12, 5, 0, DateTimeKind.Utc));
        string uncounted = model.Enemies.HeaderSummary;
        Assert.Contains("none counted", uncounted);

        model.EnemyObservations[0].Count = 2;
        Assert.NotEqual(uncounted, model.Enemies.HeaderSummary);
        model.EnemyObservations[0].Count = 0;
        Assert.Equal(uncounted, model.Enemies.HeaderSummary);

        await model.SaveRunCommand.ExecuteAsync(null);

        await using ClientDbContext db = await harness.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await db.Set<RunEnemyObservation>()
            .Where(row => row.RunId == runId)
            .ToListAsync(TestContext.Current.CancellationToken));
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
        Assert.Equal("based on 2 of 2 members sharing their location", model.Fleet.HeaderSummary);
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
