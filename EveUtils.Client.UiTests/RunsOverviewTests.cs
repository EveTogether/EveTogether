using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
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

    /// <summary>AC-4: an activity with no loot capture says so instead of showing a figure. Counter-proof: format
    /// the net as <c>LootIskNet ?? 0</c> and this goes red on "0 ISK" — a zero there reads as a valuation that was
    /// taken and came out at nothing.</summary>
    [AvaloniaFact]
    public async Task ActivityWithoutALootCapture_SaysSo_AndShowsNoZero()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await _SaveSiteRunAsync(_Dispatcher(instance), 90000001, groupCode: null, cancellationToken: cancellationToken);

        Window root = (await _PresentAsync(instance, 758, cancellationToken)).Root;
        List<string> texts = RenderedText.VisibleTexts(root);

        Assert.DoesNotContain(texts, text => text.Contains("0 ISK"));        // the criterion's own point, asserted first
        Assert.Contains(texts, text => text == "no loot capture recorded");
        Assert.Contains(texts, text => text.EndsWith("no loot captured"));   // the day band holds the same line
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
        ActivityOverviewRowViewModel row = Assert.Single(Assert.Single(viewModel.Days).Rows);
        foreach (Character character in Crew)
            Assert.Contains(character.Name, row.CrewText);

        await row.ToggleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();

        Assert.Equal(6, row.SubRuns.Count);
        Assert.Equal(6, RenderedText.VisibleTexts(root).Count(text => text.StartsWith("flew it")));
    }

    /// <summary>AC-6: a pilot with nothing running keeps their lane and their START. Counter-proof: filter the band
    /// on "has a running run" and the idle lane disappears, which this goes red on. A toon that drops out of the
    /// band is a toon you forget.</summary>
    [AvaloniaFact]
    public async Task PilotWithNothingRunning_KeepsTheirLaneAndAStart()
    {
        using var instance = TestClientInstance.Create();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        // Started and left running: the one lane that carries a run today, since RunningRunLookup answers only when
        // there is exactly one open run app-wide (ET-130 is what lifts that).
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

    private static ICqrsDispatcher _Dispatcher(TestClientInstance instance) =>
        instance.Services.GetRequiredService<ICqrsDispatcher>();

    // The control tree the operator sees when the module is docked: the host lifts window.Content out and reparents
    // it, so the assertions run against that content and never against a window that is never shown.
    private sealed record Presented(RunsWindow Window, Window Root, RunsOverviewViewModel ViewModel);

    private static async Task<(RunsWindow Window, RunsOverviewViewModel ViewModel)> _WindowAsync(
        TestClientInstance instance, double width, CancellationToken cancellationToken,
        IReadOnlyList<Character>? characters = null)
    {
        ICqrsDispatcher dispatcher = _Dispatcher(instance);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        // No lane clock: a DispatcherTimer here would go on ticking for the rest of the test session, since the
        // window that would dispose the view-model is never closed.
        var viewModel = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services,
            characters ?? Crew, runClock: false);
        await viewModel.LoadAsync(cancellationToken);
        return (new RunsWindow(viewModel) { Width = width, Height = 1400 }, viewModel);
    }

    private static async Task<Presented> _PresentAsync(
        TestClientInstance instance, double width, CancellationToken cancellationToken,
        IReadOnlyList<Character>? characters = null)
    {
        (RunsWindow window, RunsOverviewViewModel viewModel) =
            await _WindowAsync(instance, width, cancellationToken, characters);

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
        IReadOnlyList<RunParameterInput>? parameters = null)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, StartedAtUtc,
            1234, siteName, 30000142, groupCode), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, StartedAtUtc.AddMinutes(15),
            StartedAtUtc.AddMinutes(16), [], [], [], parameters ?? []), cancellationToken);
    }

    private static IReadOnlyList<RunParameterInput> _EveryRewardForm() =>
        [.. Enum.GetValues<RunParameterKey>().Select(key => new RunParameterInput
        {
            ParameterKey = key, TypedValue = "1", Amount = 84_200_000m, ObservedAtUtc = StartedAtUtc
        })];
}
