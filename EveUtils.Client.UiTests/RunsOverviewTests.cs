using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Formatting;
using EveUtils.Client.Gamelog;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-161: the runs screen in the shell. One counter-proof per acceptance criterion, taken from the ticket itself —
/// each was shown red before the screen existed.
///
/// AC-7 (the mockup in Depot brought up to date) has no automated counter-proof and is not attempted here: it is a
/// delivery criterion about a document that lives outside this repository. Its one checkable half — that no mockup
/// file appears in the pull request's diff — is a property of the diff, not of a render.
/// </summary>
public sealed class RunsOverviewTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<Character> Crew =
    [
        new("Ra Vinter", 90000001), new("Kav Orn", 90000002), new("Deio Tarn", 90000003),
        new("Nilsa Orn", 90000004), new("Bex Hale", 90000005), new("Torv Kesh", 90000006)
    ];

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    /// <summary>AC-1, both halves. The layout takes its size from the space the host gives it, and docked and
    /// floating are the very same <c>Content</c> instance because <c>ModuleHostService.Render</c> moves it between
    /// the two. Counter-proof: a layout that sizes off the window instead of the space it is handed makes the
    /// docked render wider than its host and goes red; a second layout built for the floating case goes red on the
    /// instance. Two layouts is the problem, not the fix.</summary>
    [AvaloniaFact]
    public async Task Runs_SizesOffItsHost_AndIsTheSameContentDockedAndFloating()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(_Dispatcher(instance), 90000001, groupCode: null, cancellationToken: cancellationToken);

        (RunsWindow window, _) = await _WindowAsync(instance, 758, cancellationToken);
        var display = new FakeDisplay();
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(window, "RUNS", "runs", "runs");
        var docked = (Control)Assert.Single(display.HostTabs).Content!;

        var narrowHost = new Window { Width = 758, Height = 1400, Content = docked };
        narrowHost.Show();
        Dispatcher.UIThread.RunJobs();
        narrowHost.UpdateLayout();
        Assert.True(docked.Bounds.Width <= 758.5,
            $"the docked content is {docked.Bounds.Width:F1}px wide inside a 758px host");
        narrowHost.Content = null;

        display.IsFloating = true;
        host.SwitchMode();

        Assert.Same(docked, window.Content);
    }

    /// <summary>AC-2: at the module host's own docked width nothing falls outside its row and no action button is
    /// squeezed to nothing. Counter-proof: the same layout at a wide floating width must pass too — a check that
    /// goes green at both widths without the layout being fluid is not measuring the layout.</summary>
    [AvaloniaTheory]
    [InlineData(758)]
    [InlineData(1180)]
    public async Task Rows_KeepTheirChipsInside_AndNoActionButtonCollapses(double width)
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // Six pilots, a long site name and every reward form at once: the widest row this screen can be handed.
        foreach (Character character in Crew)
            await _SaveSiteRunAsync(dispatcher, character.EsiCharacterId!.Value, "HF-7QK2", cancellationToken,
                siteName: "Sansha's Command Relay Outpost",
                parameters: character.EsiCharacterId == 90000001 ? _EveryRewardForm() : []);

        Window root = (await _PresentAsync(instance, width, cancellationToken)).Root;

        Control[] rows = [.. root.GetVisualDescendants().OfType<Control>()
            .Where(control => control.Classes.Contains("activityrow") && control.IsEffectivelyVisible)];
        Assert.NotEmpty(rows);
        foreach (Control row in rows)
        {
            double rowRight = (row.TranslatePoint(default, root) ?? default).X + row.Bounds.Width;
            foreach (Control chip in row.GetVisualDescendants().OfType<Border>()
                         .Where(border => border.Classes.Contains("chip") && border.IsEffectivelyVisible))
            {
                double chipRight = (chip.TranslatePoint(default, root) ?? default).X + chip.Bounds.Width;
                Assert.True(chipRight <= rowRight + 0.5,
                    $"a reward chip runs past its row at width {width}: {chipRight:F1} > {rowRight:F1}");
            }
        }

        foreach (Button button in root.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
            Assert.True(button.Bounds.Width > 0 && button.Bounds.Height > 0,
                $"an action button has no size at width {width}");
    }

    /// <summary>AC-3: a reward whose key this screen was never taught still gets a chip, named after the key. It
    /// neither vanishes nor takes the row down with it. Counter-proof: a closed <c>switch</c> with a <c>throw</c>
    /// on the unknown key takes the whole render down, and a silent <c>default</c> goes red on the missing
    /// chip.</summary>
    [AvaloniaFact]
    public async Task RewardKindTheScreenDoesNotKnow_StillGetsAChip()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(_Dispatcher(instance), 90000001, groupCode: null, cancellationToken: cancellationToken,
            parameters:
            [
                new RunParameterInput { ParameterKey = RunParameterKey.LoyaltyPoints, TypedValue = "1240", Amount = 1_240m, ObservedAtUtc = StartedAtUtc },
                // Not in the enum at all — what a member added after this screen was written looks like on disk.
                new RunParameterInput { ParameterKey = (RunParameterKey)41, TypedValue = "7", Amount = 7m, ObservedAtUtc = StartedAtUtc }
            ]);

        Window root = (await _PresentAsync(instance, 758, cancellationToken)).Root;
        List<string> texts = RenderedText.VisibleTexts(root);

        Assert.Contains(texts, text => text == "7 KIND 41");
        Assert.Contains(texts, text => text == "1,240 LP");
    }

    /// <summary>AC-4: an activity with nothing to value — no loot capture and no bounty line — says so instead of
    /// showing a figure. Counter-proof: format the net as <c>LootIskNet ?? 0</c> and this goes red on "0 ISK" — a
    /// zero there reads as a valuation that was taken and came out at nothing.</summary>
    [AvaloniaFact]
    public async Task ActivityWithoutALootCaptureOrBounty_SaysSo_AndShowsNoZero()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(_Dispatcher(instance), 90000001, groupCode: null, cancellationToken: cancellationToken);

        Window root = (await _PresentAsync(instance, 758, cancellationToken)).Root;
        List<string> texts = RenderedText.VisibleTexts(root);

        Assert.DoesNotContain(texts, text => text.Contains("0 ISK"));        // the criterion's own point, asserted first
        Assert.Contains(texts, text => text == "no loot or bounty recorded");
        Assert.Contains(texts, text => text.EndsWith("nothing recorded to value"));  // the day band holds the same line
    }

    /// <summary>AC-5: six saved runs under one group code are one row that names its six pilots, and unfold into
    /// six. Counter-proof: bind to <c>Run</c> instead of the overview query and a test expecting six rows on screen
    /// passes — which is exactly the wrong screen, and is what <see cref="Assert.Single{T}(IEnumerable{T})"/> here
    /// catches.</summary>
    [AvaloniaFact]
    public async Task SixRunsInOneGroup_AreOneRowThatUnfoldsIntoSix()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        foreach (Character character in Crew)
            await _SaveSiteRunAsync(dispatcher, character.EsiCharacterId!.Value, "HF-7QK2", cancellationToken);

        (_, Window root, RunsOverviewViewModel viewModel) = await _PresentAsync(instance, 758, cancellationToken);

        Assert.Single(root.GetVisualDescendants().OfType<Control>(),
            control => control.Classes.Contains("activityrow") && control.IsEffectivelyVisible);
        ActivityOverviewRowViewModel row = Assert.Single(Assert.Single(viewModel.Tabs[0].Days).Rows);
        foreach (Character character in Crew)
            Assert.Contains(character.Name, row.CrewText);

        await row.ToggleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();

        Assert.Equal(6, row.SubRuns.Count);
        Assert.Equal(6, RenderedText.VisibleTexts(root).Count(text => text.StartsWith("flew it")));
    }

    /// <summary>ET-247: a fleet mate synced in from a server never logged in on this machine, so
    /// <c>RunsOverviewViewModel</c>'s local roster (<c>_namesById</c>) has no entry for them — only the name their
    /// own run recorded at start time (ET-212) can name them. Counter-proof: naming the crew straight off the bare
    /// character ids, without the run's own snapshot, reads "character 883434905" here instead of "RaymondKrah".</summary>
    [AvaloniaFact]
    public async Task CrewText_NamesAFleetMateFromTheirRunsOwnSnapshot_NotJustTheLocalRoster()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, Crew[0].EsiCharacterId!.Value, "HF-7QK2", cancellationToken);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(883434905, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142, "HF-7QK2", CharacterNameSnapshot: "RaymondKrah"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);

        (_, _, RunsOverviewViewModel viewModel) = await _PresentAsync(instance, 758, cancellationToken);

        ActivityOverviewRowViewModel row = Assert.Single(Assert.Single(viewModel.Tabs[0].Days).Rows);
        Assert.Contains("RaymondKrah", row.CrewText);
        Assert.DoesNotContain("character 883434905", row.CrewText);
    }

    /// <summary>AC-6: a pilot with nothing running keeps their lane and their START. Counter-proof: filter the band
    /// on "has a running run" and the idle lane disappears, which this goes red on. A toon that drops out of the
    /// band is a toon you forget.</summary>
    [AvaloniaFact]
    public async Task PilotWithNothingRunning_KeepsTheirLaneAndAStart()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _Dispatcher(instance).Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);

        Window root = (await _PresentAsync(instance, 758, cancellationToken, characters: [Crew[0], Crew[5]])).Root;
        List<string> texts = RenderedText.VisibleTexts(root);

        Assert.Contains(texts, text => text == "Torv Kesh");
        Assert.Contains(texts, text => text == "nothing running");
        Assert.Contains(texts, text => text == "START");
        Assert.Contains(texts, text => text == "Homefront");   // and the busy lane still reads as busy
        Assert.Contains(texts, text => text == "OPEN");
    }

    /// <summary>ET-203, AC-2/ET-130: two characters running at once each get their own lane rather than the band
    /// answering for the app as a whole. Counter-proof: read the running run through <c>GetRunningRunQuery</c>
    /// (single, "exactly one or nothing") instead of per character, and this goes red with both lanes reading
    /// "nothing running" — two open runs is exactly the case that query answers null on.</summary>
    [AvaloniaFact]
    public async Task TwoCharactersRunningAtOnce_EachShowTheirOwnLane()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new StartRunCommand(90000002, ActivityKind.Site, StartedAtUtc,
            5678, "Sanctum", 30000143), cancellationToken);

        (_, RunsOverviewViewModel viewModel) = await _WindowAsync(
            instance, 758, cancellationToken, characters: [Crew[0], Crew[1]]);

        RunningLaneViewModel first = viewModel.Lanes.Single(lane => lane.Character.EsiCharacterId == 90000001);
        RunningLaneViewModel second = viewModel.Lanes.Single(lane => lane.Character.EsiCharacterId == 90000002);
        Assert.True(first.IsRunning);
        Assert.Equal("Homefront", first.StateText);
        Assert.True(second.IsRunning);
        Assert.Equal("Sanctum", second.StateText);
    }

    /// <summary>ET-203's root cause, reproduced directly. Measured against the code rather than assumed: a run
    /// <em>stopped</em> and never saved is filtered out of <c>RunningRunLookup</c> before this screen's old query
    /// ever saw it (<c>GetRunningRunQueryHandler</c> asks without <c>includeStopped</c>), so that shape alone was
    /// never this bug. What does reproduce it is a second row still on <c>Running</c> that nothing local ever shows
    /// a lane for — orphaned by a crash before <c>SetRunStoppedCommand</c> could run, say — sitting beside a
    /// character who is plainly, actually running right now. The old single-run query counts both, finds two
    /// candidates, and answers null for the whole app; the lane that is genuinely running reads "nothing running"
    /// right along with everyone else's. Counter-proof: keep reading the band through that single-run query and
    /// this goes red on the real lane.</summary>
    [AvaloniaFact]
    public async Task AStaleRunningRowForAnUnlistedCharacter_DoesNotBlockARealRunningLaneFromShowing()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // Left running on purpose, and for a character this screen's own roster never includes below — this is the
        // orphaned row, not a second lane under test.
        await dispatcher.Send(new StartRunCommand(90000003, ActivityKind.Site, StartedAtUtc,
            9999, "Abandoned Site", 30000144), cancellationToken);
        await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            5678, "Sanctum", 30000143), cancellationToken);

        (_, RunsOverviewViewModel viewModel) = await _WindowAsync(
            instance, 758, cancellationToken, characters: [Crew[0]]);

        RunningLaneViewModel running = Assert.Single(viewModel.Lanes);
        Assert.True(running.IsRunning);
        Assert.Equal("Sanctum", running.StateText);
        Assert.Equal("OPEN", running.ActionText);
    }

    /// <summary>ET-203's second track: a run starting elsewhere while the overview is already open must reach the
    /// band without the RUNS entry being reopened — the same "screen open, event fired" gap ET-189 closed for the
    /// day bands. Counter-proof: a view model with no <c>RunStartedEvent</c> subscription never revisits its lanes,
    /// so this reads START forever.</summary>
    [AvaloniaFact]
    public async Task RunStartedWhileScreenIsOpen_TakesOverItsLaneWithoutReopening()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (_, RunsOverviewViewModel viewModel) = await _WindowAsync(
            instance, 758, cancellationToken, characters: [Crew[0]]);
        RunningLaneViewModel lane = Assert.Single(viewModel.Lanes);
        Assert.False(lane.IsRunning);

        await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        Dispatcher.UIThread.RunJobs(); // the refresh is posted to the UI thread, not run inline

        Assert.True(lane.IsRunning);
        Assert.Equal("Homefront", lane.StateText);
        Assert.Equal("OPEN", lane.ActionText);
    }

    /// <summary>ET-203's second track, the other direction: stopping a run while the overview sits open must drop
    /// its lane back to idle without a reopen — the clock keeps ticking via <c>_OnClockTick</c> alone otherwise,
    /// since that only advances a lane already attached rather than noticing the run underneath it stopped.</summary>
    [AvaloniaFact]
    public async Task RunStoppedWhileScreenIsOpen_FallsBackToIdleWithoutReopening()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);

        (_, RunsOverviewViewModel viewModel) = await _WindowAsync(
            instance, 758, cancellationToken, characters: [Crew[0]]);
        RunningLaneViewModel lane = Assert.Single(viewModel.Lanes);
        Assert.True(lane.IsRunning);

        await dispatcher.Send(new SetRunStoppedCommand(started.Value, DateTime.UtcNow), cancellationToken);
        Dispatcher.UIThread.RunJobs(); // the refresh is posted to the UI thread, not run inline

        Assert.False(lane.IsRunning);
        Assert.Equal("nothing running", lane.StateText);
        Assert.Equal("START", lane.ActionText);
    }

    /// <summary>ET-221 AC-2's counterproof: pressing START on an idle character card opens the manual-start dialog
    /// with every registered character offered — not just the one card's own character, which is all the dialog
    /// could ever take before ET-221 — and that card's own character ticked, so hitting START without touching the
    /// picker still starts exactly that one character's run.</summary>
    [AvaloniaFact]
    public async Task StartOnALane_OffersEveryCharacter_WithThatLanesCharacterPreselected()
    {
        using var instance = TestClientInstance.Create();
        ICharacterRegistry registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        foreach (Character character in Crew.Take(2))
            await registry.AddOrUpdateAsync(character, TestContext.Current.CancellationToken);
        var dialogs = new RecordingDialogService();

        (_, RunsOverviewViewModel viewModel) = await _WindowAsync(
            instance, 758, TestContext.Current.CancellationToken, characters: Crew.Take(2).ToList(), dialogs: dialogs);
        RunningLaneViewModel lane = viewModel.Lanes.Single(l => l.Character.EsiCharacterId == Crew[1].EsiCharacterId);

        await lane.ActCommand.ExecuteAsync(null);

        ManualRunStartViewModel opened = dialogs.LastManualRunStart!;
        Assert.NotNull(opened);
        Assert.Equal(2, opened.Characters.Count);
        Character selected = Assert.Single(opened.SelectedCharacters);
        Assert.Equal(Crew[1].EsiCharacterId, selected.EsiCharacterId);
    }

    /// <summary>ET-220: DISCARD stops a running lane exactly like STOP does, but nothing told this screen so — the
    /// lane kept ticking until the RUNS entry was reopened, which is what the pilot reported. Counter-proof: a
    /// <c>DiscardRunCommandHandler</c> that never publishes <c>RunRunningStateChangedEvent</c> (the shape before
    /// this fix) leaves the lane reading as running here.</summary>
    [AvaloniaFact]
    public async Task RunDiscardedWhileScreenIsOpen_FallsBackToIdleWithoutReopening()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);

        (_, RunsOverviewViewModel viewModel) = await _WindowAsync(
            instance, 758, cancellationToken, characters: [Crew[0]]);
        RunningLaneViewModel lane = Assert.Single(viewModel.Lanes);
        Assert.True(lane.IsRunning);

        await dispatcher.Send(
            new DiscardRunCommand(started.Value, DateTime.UtcNow, DeleteAfterDiscard: true), cancellationToken);
        Dispatcher.UIThread.RunJobs(); // the refresh is posted to the UI thread, not run inline

        Assert.False(lane.IsRunning);
        Assert.Equal("nothing running", lane.StateText);
        Assert.Equal("START", lane.ActionText);
    }

    /// <summary>ET-220 round 2: Jithran's own report — clicking Undo on the discard toast did not put the run back in
    /// UNFINISHED until he reopened the RUNS entry. Cause, measured: <c>RunRestoredEvent</c>'s subscription only
    /// refilled the day bands (<c>_OnRunSavedAsync</c>), never <c>UnfinishedRuns</c> — harmless for ET-214's own
    /// restore, which only ever reached a Saved run that was never in that band to begin with. Counter-proof: a
    /// subscription that still only refills the day bands (the shape before this fix) leaves <c>UnfinishedRuns</c>
    /// empty here.</summary>
    [AvaloniaFact]
    public async Task UndoingADiscard_PutsTheRunBackInUnfinishedWithoutReopening()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site, StartedAtUtc,
            1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(
            new DiscardRunCommand(started.Value, StartedAtUtc.AddMinutes(5), DeleteAfterDiscard: true), cancellationToken);

        (_, RunsOverviewViewModel viewModel) = await _WindowAsync(instance, 758, cancellationToken);
        Assert.Empty(viewModel.UnfinishedRuns);

        await dispatcher.Send(new RestoreRunCommand(started.Value), cancellationToken);
        Dispatcher.UIThread.RunJobs(); // the refresh is posted to the UI thread, not run inline

        UnfinishedRunViewModel run = Assert.Single(viewModel.UnfinishedRuns);
        Assert.Equal("Homefront", run.SiteText);
    }

    /// <summary>
    /// Acceptatie 2026-09-04, bevinding 1: "net" is what the activity brought in, and on a combat site that is
    /// mostly bounty. Measured on a copy of the operator's own store, the screen read 6.777 ISK where the same
    /// eleven activities held 1.258.941 ISK of bounty besides, and the day band called a million-ISK evening
    /// "+6.8k ISK net".
    ///
    /// Counter-proof: take <c>BountyIsk</c> back out of <c>ActivityOverviewRowDto</c> and the row falls to the
    /// loot alone — here no loot was ever captured, so the row reads "no loot or bounty recorded" and the day band
    /// "nothing recorded to value", both on an activity that paid out 1.258.941 ISK.
    /// </summary>
    [AvaloniaFact]
    public async Task Overview_BountyCountsTowardsTheNet_InTheRowAndInTheDayBand()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(_Dispatcher(instance), 90000001, groupCode: null, cancellationToken: cancellationToken,
            bounties: [
                new RunBountyEntryInput { OccurredAtUtc = StartedAtUtc.AddMinutes(2), Isk = 1_000_000m },
                new RunBountyEntryInput { OccurredAtUtc = StartedAtUtc.AddMinutes(9), Isk = 258_941m }
            ]);

        Presented presented = await _PresentAsync(instance, 758, cancellationToken);
        RunsDayViewModel day = Assert.Single(presented.ViewModel.Tabs[0].Days);
        ActivityOverviewRowViewModel row = Assert.Single(day.Rows);

        Assert.True(row.HasNet);
        Assert.Equal(1_258_941m, row.NetIsk);
        Assert.Equal("+1.26M ISK", row.NetText);
        Assert.Contains("+1.26M ISK net", day.SummaryText);

        List<string> texts = RenderedText.VisibleTexts(presented.Root);
        Assert.Contains(texts, text => text == "+1.26M ISK");
        Assert.DoesNotContain(texts, text => text == "no loot or bounty recorded");
    }

    [AvaloniaFact]
    public async Task DayToggle_HidesAndRestoresItsActivityRows()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(_Dispatcher(instance), 90000001, groupCode: null, cancellationToken: cancellationToken);

        Presented presented = await _PresentAsync(instance, 758, cancellationToken);
        ToggleButton toggle = presented.Root.GetVisualDescendants().OfType<ToggleButton>()
            .Single(control => control.Classes.Contains("dayband"));
        Control row = presented.Root.GetVisualDescendants().OfType<Control>()
            .Single(control => control.Classes.Contains("activityrow"));

        toggle.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        presented.Root.UpdateLayout();

        Assert.False(row.IsEffectivelyVisible);

        toggle.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        presented.Root.UpdateLayout();

        Assert.True(row.IsEffectivelyVisible);
    }

    /// <summary>ET-179 AC-1: three runs on <c>Stopped</c> and two on <c>Saved</c> — all five are on screen, and the
    /// three that were never finished stand apart from the two that were. Counter-proof: read only
    /// <c>ActivitySummary</c>, which is built from saved runs alone, and the three vanish — the behaviour of the
    /// day this was written, where eleven of them had piled up unseen in the operator's own store.</summary>
    [AvaloniaFact]
    public async Task StoppedRunsThatWereNeverFinished_AreOnScreen_ApartFromTheSavedOnes()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        foreach (Character character in Crew.Take(3))
            await _StopSiteRunAsync(dispatcher, character.EsiCharacterId!.Value, cancellationToken);
        foreach (Character character in Crew.Skip(3).Take(2))
            await _SaveSiteRunAsync(dispatcher, character.EsiCharacterId!.Value, null, cancellationToken);

        Presented presented = await _PresentAsync(instance, 758, cancellationToken);
        List<string> texts = RenderedText.VisibleTexts(presented.Root);

        Assert.Equal(3, presented.ViewModel.UnfinishedRuns.Count);
        Assert.Equal(2, Assert.Single(presented.ViewModel.Tabs[0].Days).Rows.Count);
        Assert.Contains(texts, text => text == "UNFINISHED");
        Assert.Equal(3, texts.Count(text => text == "SAVE"));
    }

    /// <summary>ET-179 AC-2: both ways out work from this screen, and the row is gone from the unfinished band
    /// afterwards — saved it stands under its day, thrown away it stands nowhere. Counter-proof: update the run but
    /// leave the screen as it was and the row stays where it is, which the first assertion catches.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnfinishedRun_SavedOrThrownAway_LeavesTheBand(bool save)
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _StopSiteRunAsync(_Dispatcher(instance), 90000001, cancellationToken);
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) };

        Presented presented = await _PresentAsync(instance, 758, cancellationToken, dialogs: dialogs);
        UnfinishedRunViewModel run = Assert.Single(presented.ViewModel.UnfinishedRuns);
        await (save ? run.SaveCommand : run.DeleteCommand).ExecuteAsync(null);

        Assert.Empty(presented.ViewModel.UnfinishedRuns);
        if (!save)
        {
            Assert.Empty(presented.ViewModel.Tabs[0].Days);
            return;
        }

        ActivityOverviewRowViewModel row = Assert.Single(Assert.Single(presented.ViewModel.Tabs[0].Days).Rows);
        Assert.Equal("Homefront", row.SiteText);
    }

    /// <summary>ET-217 AC-1/AC-2: an unfinished row shows what its run earned so far, added up the exact same way
    /// IskContributors adds up the run window's own TOTAL ISK and a saved activity's — an ISK-shaped mission
    /// reward parameter here, formatted through the same IskFormat the rest of the app uses. Counter-proof: read
    /// TotalIskText off a row built without wiring UnfinishedRunDto.TotalIsk through (the shape of this ticket before
    /// the fix) and this goes red on "0 ISK" instead of the reward's own amount.</summary>
    [AvaloniaFact]
    public async Task UnfinishedRun_WithAKnownReward_ShowsItsTotalIsk()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTime stoppedAtUtc = DateTime.UtcNow.AddHours(-1);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Mission,
            stoppedAtUtc.AddMinutes(-15), 1234, "Homefront", 30000142,
            Parameters:
            [
                new RunParameterInput
                {
                    ParameterKey = RunParameterKey.Isk, TypedValue = "1", Amount = 12_345_678m,
                    ObservedAtUtc = stoppedAtUtc
                }
            ]), cancellationToken);
        await dispatcher.Send(new SetRunStoppedCommand(started.Value, stoppedAtUtc), cancellationToken);

        Presented presented = await _PresentAsync(instance, 758, cancellationToken);
        UnfinishedRunViewModel run = Assert.Single(presented.ViewModel.UnfinishedRuns);

        Assert.Equal(IskFormat.Whole(12_345_678m), run.TotalIskText);
        Assert.False(run.TotalIskUnknown);
    }

    /// <summary>ET-219, making good on the promise ET-217 left open: <c>GetUnfinishedRunsQueryHandler</c> already
    /// reads <c>run.BountyEntries.Sum(...)</c>, not a hardcoded zero, on the understanding that "the moment a future
    /// session persists bounty before SAVE, it counts here without any change." Counter-proof: a run stopped after
    /// earning bounty live through <see cref="GamelogClientService"/> — no SAVE, no <c>RunParameterInput</c> reward
    /// — still shows that bounty as its TOTAL ISK.</summary>
    [AvaloniaFact]
    public async Task UnfinishedRun_WithLiveBounty_IncludesItInTotalIsk()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTime stoppedAtUtc = DateTime.UtcNow.AddHours(-1);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site,
            stoppedAtUtc.AddMinutes(-15), 1234, "Homefront", 30000142), cancellationToken);

        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(90000001, "Ra Vinter");
        await gamelog.AddBountyAsync("Ra Vinter", new BountyEvent(stoppedAtUtc.AddMinutes(-5), 4_500_000));

        await dispatcher.Send(new SetRunStoppedCommand(started.Value, stoppedAtUtc), cancellationToken);

        Presented presented = await _PresentAsync(instance, 758, cancellationToken);
        UnfinishedRunViewModel run = Assert.Single(presented.ViewModel.UnfinishedRuns);

        Assert.Equal(IskFormat.Whole(4_500_000m), run.TotalIskText);
        Assert.False(run.TotalIskUnknown);
    }

    /// <summary>ET-217 AC-4, reopened 2026-09-10: Jithran read a bare "ISK" on a run with nothing to show, which is
    /// what IskFormat.Exact's "— ISK" looked like to him — indistinguishable from a rendering glitch, not a plain
    /// zero. A run with no loot and no bounty is a real, known zero, so it must say "0 ISK" outright. Counter-proof:
    /// this goes red against the pre-fix reading of "— ISK" (verified by reverting UnfinishedRunViewModel.TotalIskText
    /// to IskFormat.Exact and rerunning before restoring it).</summary>
    [AvaloniaFact]
    public async Task UnfinishedRun_WithNoLootAndNoBounty_ShowsZeroIsk()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _StopSiteRunAsync(_Dispatcher(instance), 90000001, cancellationToken);

        Presented presented = await _PresentAsync(instance, 758, cancellationToken);
        UnfinishedRunViewModel run = Assert.Single(presented.ViewModel.UnfinishedRuns);

        Assert.Equal("0 ISK", run.TotalIskText);
        Assert.False(run.TotalIskUnknown);
    }

    /// <summary>ET-217 review: a run that captured loot nobody has priced yet is a different thing from a run that
    /// earned nothing — showing "0 ISK" there would claim an answer nobody has. Counter-proof: the row reads a
    /// dedicated unknown state instead of a number, and TotalIskUnknown says so for the view to style differently.
    /// </summary>
    [AvaloniaFact]
    public async Task UnfinishedRun_WithUnpricedLootAndNoOtherEarnings_ShowsUnknown()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTime stoppedAtUtc = DateTime.UtcNow.AddHours(-1);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(90000001, ActivityKind.Site,
            stoppedAtUtc.AddMinutes(-15), 1234, "Homefront", 30000142), cancellationToken);
        // Type id 999999999 is deliberately not in any price fixture, so this loot has no known value.
        await dispatcher.Send(new AddRunLootCaptureCommand(new RunLootCaptureInput
        {
            CapturedAtUtc = stoppedAtUtc, Source = LootCaptureSource.Clipboard, Role = LootCaptureRole.Snapshot,
            Entries =
            [
                new RunLootEntryInput
                {
                    ItemTypeId = 999999999, Name = "Unpriced Widget", Quantity = 1, LootKind = LootKind.Gained
                }
            ]
        }), cancellationToken);
        await dispatcher.Send(new SetRunStoppedCommand(started.Value, stoppedAtUtc), cancellationToken);

        Presented presented = await _PresentAsync(instance, 758, cancellationToken);
        UnfinishedRunViewModel run = Assert.Single(presented.ViewModel.UnfinishedRuns);

        Assert.Equal("not priced yet", run.TotalIskText);
        Assert.True(run.TotalIskUnknown);
    }

    /// <summary>ET-179 AC-3: the runs that were saved are shown as they always were. An evening is what was
    /// committed to it, so three stopped runs beside it change neither its count nor its total. Counter-proof: add
    /// the unfinished runs to the same list and the band reads five activities.</summary>
    [AvaloniaFact]
    public async Task StoppedRuns_DoNotReachTheDayBand()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        foreach (Character character in Crew.Take(3))
            await _StopSiteRunAsync(dispatcher, character.EsiCharacterId!.Value, cancellationToken);
        foreach (Character character in Crew.Skip(3).Take(2))
            await _SaveSiteRunAsync(dispatcher, character.EsiCharacterId!.Value, null, cancellationToken,
                bounties: [new RunBountyEntryInput { OccurredAtUtc = StartedAtUtc.AddMinutes(2), Isk = 500_000m }]);

        Presented presented = await _PresentAsync(instance, 758, cancellationToken);
        RunsDayViewModel day = Assert.Single(presented.ViewModel.Tabs[0].Days);

        Assert.Equal("2 activities · 0:30:00 flown · +1M ISK net", day.SummaryText);
    }

    /// <summary>ET-179: <c>Stopped</c> is not a resting place (Raymond, 2026-09-04). A day after it was stopped the
    /// app commits the run as it stands and says on the row that it did; an hour after, it is still the pilot's own
    /// to finish. Counter-proof, one per row: save every stopped run without a deadline and the hour-old one is
    /// taken out of the pilot's hands; leave the deadline unjudged here and the day-old one sits in the band for
    /// good, which is the pile this ticket started from.</summary>
    [AvaloniaTheory]
    [InlineData(25)]
    [InlineData(1)]
    public async Task RunLeftUnfinished_IsSavedByItself_OnlyOnceItIsADayOld(double hoursSinceStop)
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _StopSiteRunAsync(_Dispatcher(instance), 90000001, cancellationToken, hoursSinceStop);

        Presented presented = await _PresentAsync(instance, 758, cancellationToken);

        if (hoursSinceStop < 24)
        {
            Assert.Single(presented.ViewModel.UnfinishedRuns);
            Assert.Empty(presented.ViewModel.Tabs[0].Days);
            return;
        }

        Assert.Empty(presented.ViewModel.UnfinishedRuns);
        ActivityOverviewRowViewModel row = Assert.Single(Assert.Single(presented.ViewModel.Tabs[0].Days).Rows);
        Assert.True(row.HasAutoSavedRun);
        Assert.Contains(RenderedText.VisibleTexts(presented.Root), text => text == "auto-saved");
    }

    private static ICqrsDispatcher _Dispatcher(TestClientInstance instance) =>
        instance.Services.GetRequiredService<ICqrsDispatcher>();

    // The control tree the operator sees when the module is docked: the host lifts window.Content out and reparents
    // it, so the assertions run against that content and never against a window that is never shown.
    private sealed record Presented(RunsWindow Window, Window Root, RunsOverviewViewModel ViewModel);

    private static async Task<(RunsWindow Window, RunsOverviewViewModel ViewModel)> _WindowAsync(
        TestClientInstance instance, double width, CancellationToken cancellationToken,
        IReadOnlyList<Character>? characters = null, RecordingDialogService? dialogs = null)
    {
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        // No lane clock: a DispatcherTimer here would go on ticking for the rest of the test session, since the
        // window that would dispose the view-model is never closed.
        var viewModel = new RunsOverviewViewModel(dispatcher, dialogs ?? new RecordingDialogService(), instance.Services,
            characters ?? Crew, runClock: false);
        await viewModel.LoadAsync(cancellationToken);
        return (new RunsWindow(viewModel) { Width = width, Height = 1400 }, viewModel);
    }

    /// <summary>ET-189: a run saved elsewhere while the screen is already open appears without the RUNS entry being
    /// reopened — the specific case the ticket names, the first run of a day, with no day band on screen yet to
    /// hold it. Counter-proof: <c>RunsOverviewViewModel</c> without an <c>IEventBus</c> subscription never rebuilds
    /// its days on its own, so this reads zero rows until something calls <c>RefreshModule</c>/<c>LoadAsync</c>
    /// again — which this test never does.</summary>
    [AvaloniaFact]
    public async Task RunSavedWhileScreenIsOpen_AppearsWithoutReopening()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        (_, RunsOverviewViewModel viewModel) = await _WindowAsync(instance, 758, cancellationToken);
        Assert.Empty(viewModel.Tabs[0].Days);

        await _SaveSiteRunAsync(dispatcher, 90000001, groupCode: null, cancellationToken: cancellationToken);
        Dispatcher.UIThread.RunJobs(); // the refresh is posted to the UI thread, not run inline (ET-189 review)

        ActivityOverviewRowViewModel row = Assert.Single(Assert.Single(viewModel.Tabs[0].Days).Rows);
        Assert.Equal("Homefront", row.SiteText);
    }

    /// <summary>ET-214 round 2: deleting an activity from its own detail screen must not leave this screen's row and
    /// day total stale if it happens to be open at the same time — the same "screen open, event fired" gap ET-189
    /// closed for saves, now measured for <c>RunDeletedEvent</c>. Counter-proof: without that subscription this
    /// reads the deleted row forever, since nothing else here ever asks the overview to reload.</summary>
    [AvaloniaFact]
    public async Task RunDeletedFromItsDetailScreenWhileOverviewIsOpen_RemovesTheRowWithoutReopening()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(_Dispatcher(instance), 90000001, groupCode: null, cancellationToken: cancellationToken);
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(true) };

        Presented presented = await _PresentAsync(instance, 758, cancellationToken, dialogs: dialogs);
        ActivityOverviewRowViewModel row = Assert.Single(Assert.Single(presented.ViewModel.Tabs[0].Days).Rows);

        // The exact path a real click takes: the row opens the detail through the very IDialogService the overview
        // itself was built with, so the confirm below answers the same dialogs field either screen would ask.
        await row.OpenDetailCommand.ExecuteAsync(null);
        ActivityDetailViewModel detail = dialogs.LastActivityDetail!;
        await detail.LoadAsync(cancellationToken);

        await detail.DeleteCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs(); // the refresh is posted to the UI thread, not run inline (ET-189 review)

        Assert.Empty(presented.ViewModel.Tabs[0].Days);
    }

    /// <summary>ET-191's per-day expand toggle must survive the live refresh ET-189 adds: a second run landing on a
    /// day the operator collapsed must not spring it back open, and must still land in that day's own total.
    /// Counter-proof: refill every day band from scratch without carrying its <c>IsExpanded</c> over (the naive
    /// version of the fix) and this goes red on the collapsed day snapping back to expanded.</summary>
    [AvaloniaFact]
    public async Task DayCollapsedByOperator_StaysCollapsed_AcrossALiveRefresh()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, groupCode: null, cancellationToken: cancellationToken);

        (_, RunsOverviewViewModel viewModel) = await _WindowAsync(instance, 758, cancellationToken);
        RunsDayViewModel day = Assert.Single(viewModel.Tabs[0].Days);
        day.IsExpanded = false;

        await _SaveSiteRunAsync(dispatcher, 90000002, groupCode: null, cancellationToken: cancellationToken);
        Dispatcher.UIThread.RunJobs(); // the refresh is posted to the UI thread, not run inline (ET-189 review)

        RunsDayViewModel refreshedDay = Assert.Single(viewModel.Tabs[0].Days);
        Assert.False(refreshedDay.IsExpanded);
        Assert.Equal(2, refreshedDay.Rows.Count);
    }

    /// <summary>ET-199: the overview must not make him scroll through weeks of history to reach today. Counter-proof:
    /// default every fresh <c>RunsDayViewModel</c> to expanded (the behaviour before this ticket) and the three-day-
    /// old evening comes up open beside the current one.</summary>
    [AvaloniaFact]
    public async Task Overview_OpensWithOnlyTheMostRecentDayExpanded()
    {
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(dispatcher, 90000001, groupCode: null, cancellationToken: cancellationToken,
            startedAtUtc: StartedAtUtc.AddDays(-3));
        await _SaveSiteRunAsync(dispatcher, 90000002, groupCode: null, cancellationToken: cancellationToken);

        Presented presented = await _PresentAsync(instance, 758, cancellationToken);

        Assert.Equal(2, presented.ViewModel.Tabs[0].Days.Count);
        RunsDayViewModel mostRecent = presented.ViewModel.Tabs[0].Days.MaxBy(day => day.Day)!;
        RunsDayViewModel older = presented.ViewModel.Tabs[0].Days.Single(day => day.Day != mostRecent.Day);
        Assert.True(mostRecent.IsExpanded);
        Assert.False(older.IsExpanded);
    }

    private static async Task<Presented> _PresentAsync(
        TestClientInstance instance, double width, CancellationToken cancellationToken,
        IReadOnlyList<Character>? characters = null, RecordingDialogService? dialogs = null)
    {
        (RunsWindow window, RunsOverviewViewModel viewModel) =
            await _WindowAsync(instance, width, cancellationToken, characters, dialogs);

        var display = new FakeDisplay();
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(window, "RUNS", "runs", "runs");

        var content = (Control)Assert.Single(display.HostTabs).Content!;
        var root = new Window { Width = width, Height = 1400, Content = content };
        root.Show();
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        return new Presented(window, root, viewModel);
    }

    private static async Task _SaveSiteRunAsync(ICqrsDispatcher dispatcher, long characterId, string? groupCode,
        CancellationToken cancellationToken, string siteName = "Homefront",
        IReadOnlyList<RunParameterInput>? parameters = null,
        IReadOnlyList<RunBountyEntryInput>? bounties = null, DateTime? startedAtUtc = null)
    {
        DateTime startedAt = startedAtUtc ?? StartedAtUtc;
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, startedAt,
            1234, siteName, 30000142, groupCode), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAt.AddMinutes(15),
            startedAt.AddMinutes(16), [], bounties ?? [], [], parameters ?? []), cancellationToken);
    }

    /// <summary>A run stopped and left there — the shape ET-179 is about: <c>Stopped</c>, never saved, never thrown
    /// away. Placed against the wall clock and not against <see cref="StartedAtUtc"/>, because how long ago it was
    /// stopped is what decides whether the app saves it by itself.</summary>
    private static async Task _StopSiteRunAsync(ICqrsDispatcher dispatcher, long characterId,
        CancellationToken cancellationToken, double hoursSinceStop = 1)
    {
        DateTime stoppedAtUtc = DateTime.UtcNow.AddHours(-hoursSinceStop);
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site,
            stoppedAtUtc.AddMinutes(-15), 1234, "Homefront", 30000142), cancellationToken);
        await dispatcher.Send(new SetRunStoppedCommand(started.Value, stoppedAtUtc), cancellationToken);
    }

    private static IReadOnlyList<RunParameterInput> _EveryRewardForm() =>
        [.. Enum.GetValues<RunParameterKey>().Select(key => new RunParameterInput
        {
            ParameterKey = key, TypedValue = "1", Amount = 84_200_000m, ObservedAtUtc = StartedAtUtc
        })];
}
