using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Earnings;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-516: where the panel stands in the run window, in numbers: under the clock and above the sections, as wide as the
/// window, its bar inside its padding, nothing in it in the dim ink (4.0-4.7:1 on the section body, under the 4.5:1 a
/// small figure needs). No picture is taken; a layout claim is a Bounds claim.
/// </summary>
public sealed class ExpectedEarningsLayoutTests
{
    private static readonly Color DimInk = Color.Parse("#FF8A7E6B");

    [AvaloniaFact]
    public async Task ThePanel_StandsUnderTheClock_AboveTheSections_AndOpenByDefault()
    {
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services =>
            services.AddSingleton<IExpectedEarningsSource>(new FixedSource(Figures(runs: 25))));
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.ApplySignatureAsync("AAA-001", "Combat Site", "Angel Sanctum", [new SdeSite(2311, "Angel Sanctum", 1, null, null, null, null, null, false, [])]);
        ActivityWindow window = Open(model);

        Border panel = Find<Border>(window, "ExpectedEarningsPanel");
        Border body = Find<Border>(window, "ExpectedEarningsBody");
        ItemsControl sections = Find<ItemsControl>(window, "SectionList");
        TextBlock clock = Find<TextBlock>(window, "ClockFigure");
        EarningsBandBar bar = Inside<EarningsBandBar>(window, "BandBar");

        Assert.True(panel.IsVisible);
        Assert.True(body.IsVisible);
        Assert.True(model.ExpectedEarnings!.IsExpanded);
        Assert.True(Top(clock, window) < Top(panel, window));
        Assert.True(Top(panel, window) + panel.Bounds.Height <= Top(sections, window) + 0.5);
        Assert.Equal(EarningsBandBar.BarHeight, bar.Bounds.Height);
        Assert.True(bar.Bounds.Width > 150, $"bar is {bar.Bounds.Width} wide");
        Assert.InRange(bar.Bounds.Width, panel.Bounds.Width - 28.5, panel.Bounds.Width - 27.5);
        Assert.False(string.IsNullOrWhiteSpace(Inside<TextBlock>(window, "TypicalFigure").Text));
    }

    [AvaloniaFact]
    public async Task FoldingThePanel_KeepsTheAnswerInTheHeader()
    {
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services =>
            services.AddSingleton<IExpectedEarningsSource>(new FixedSource(Figures(runs: 25))));
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.ApplySignatureAsync("AAA-001", "Combat Site", "Angel Sanctum", [new SdeSite(2311, "Angel Sanctum", 1, null, null, null, null, null, false, [])]);
        ActivityWindow window = Open(model);

        model.ExpectedEarnings!.IsExpanded = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(Find<Border>(window, "ExpectedEarningsBody").IsVisible);
        TextBlock summary = window.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("eesummary"));
        Assert.True(summary.IsVisible);
        Assert.Matches(@"^Typical ", summary.Text);
    }

    [AvaloniaFact]
    public async Task NoKey_LeavesNoPanelOnScreen()
    {
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services =>
            services.AddSingleton<IExpectedEarningsSource>(new FixedSource(Figures(runs: 25))));
        ActivityWindowViewModel model = await harness.OpenAsync();
        ActivityWindow window = Open(model);

        Assert.False(Find<Border>(window, "ExpectedEarningsPanel").IsVisible);
    }

    [AvaloniaFact]
    public async Task TooFewRuns_ShowsTheCountAndNoFigure()
    {
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services =>
            services.AddSingleton<IExpectedEarningsSource>(new FixedSource(Figures(runs: 3))));
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.ApplySignatureAsync("AAA-001", "Combat Site", "Angel Sanctum", [new SdeSite(2311, "Angel Sanctum", 1, null, null, null, null, null, false, [])]);
        ActivityWindow window = Open(model);

        Assert.False(Find<EarningsRangeStrip>(window, "ExpectedEarningsFigures").IsVisible);
        Assert.True(Find<StackPanel>(window, "ExpectedEarningsTooFew").IsVisible);
        Assert.Equal("Not enough runs yet — 3 of 5 needed", Find<TextBlock>(window, "TooFewLine").Text);
    }

    [AvaloniaFact]
    public async Task NoTextInThePanel_IsInTheDimInk()
    {
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services =>
            services.AddSingleton<IExpectedEarningsSource>(new FixedSource(Figures(runs: 25))));
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.ApplySignatureAsync("AAA-001", "Combat Site", "Angel Sanctum", [new SdeSite(2311, "Angel Sanctum", 1, null, null, null, null, null, false, [])]);
        model.ExpectedEarnings!.UpdateCurrentIsk(30_000_000m);
        ActivityWindow window = Open(model);

        TextBlock[] texts = [.. Find<Border>(window, "ExpectedEarningsPanel").GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsVisible && !string.IsNullOrEmpty(text.Text))];

        Assert.NotEmpty(texts);
        Assert.All(texts, text =>
        {
            Color ink = Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color;
            Assert.NotEqual(DimInk, ink);
        });
    }

    /// <summary>Two servers answered: the source chip becomes a button that opens the list inside the panel, a pick switches the
    /// chip, and everything stays inside the panel's padding. Alone, the chip is the plain one and there is no button.</summary>
    [AvaloniaFact]
    public async Task SeveralServers_TheChipOpensTheList_InsideThePanel()
    {
        ExpectedEarnings krah = Figures(runs: 25) with { Basis = EarningsBasis.AllPilots, ServerName = "Krah Server", ServerAddress = "https://krah.invalid" };
        ExpectedEarnings alliance = Figures(runs: 12) with { Basis = EarningsBasis.AllPilots, ServerName = "Alliance Server", ServerAddress = "https://alliance.invalid" };
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services =>
        {
            services.AddSingleton<IExpectedEarningsSource>(new AnsweringSource(Figures(runs: 5), new SharedEarningsAttempt(krah, OwnRunsReason.None, [krah, alliance])));
            services.AddSingleton<ISharedServerPicks>(new MemoryPicks());
        });
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.ApplySignatureAsync("AAA-001", "Combat Site", "Angel Sanctum", [new SdeSite(2311, "Angel Sanctum", 1, null, null, null, null, null, false, [])]);
        await ActivityWindowHarness.WaitUntil(() => model.ExpectedEarnings?.HasServerChoice == true);
        ActivityWindow window = Open(model);

        Button chip = window.GetVisualDescendants().OfType<Button>().First(button => button.Name == "SourceChipButton");
        ItemsControl list = Find<ItemsControl>(window, "ServerChoiceList");
        Border panel = Find<Border>(window, "ExpectedEarningsPanel");
        Assert.True(chip.IsVisible);
        Assert.False(list.IsVisible);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Name == "SourceChipText" && text.IsEffectivelyVisible);

        chip.Command?.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.True(list.IsVisible);
        Assert.Equal(2, model.ExpectedEarnings!.ServerOptions.Count);
        Assert.True(Top(list, window) >= Top(panel, window));
        Assert.True(Top(list, window) + list.Bounds.Height <= Top(panel, window) + panel.Bounds.Height + 0.5);
        Assert.InRange(list.Bounds.Width, panel.Bounds.Width - 28.5, panel.Bounds.Width - 27.5);
        Assert.True(chip.Bounds.Right <= panel.Bounds.Width + 0.5);

        await model.ExpectedEarnings!.ServerOptions.Single(option => !option.IsCurrent).ChooseCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("ALL PILOTS · Alliance Server", model.ExpectedEarnings.SourceChip);
        Assert.False(list.IsVisible);
    }

    /// <summary>ET-521: the foot holds "updated … ago" and DETAILS inside the panel's padding, under the figures; DETAILS and a click
    /// on the panel's text both open the figures of the activity, while a click on a button in it does only its own thing. The
    /// pilot who shares nothing gets the link to Settings, in a readable ink. Counter-proof: open from the whole panel including its
    /// buttons and the sharing link opens the figures too.</summary>
    [AvaloniaFact]
    public async Task TheFoot_HoldsTheAgeAndDetails_AndThePanelOpensTheFigures()
    {
        var screens = new RecordingEarningsScreens();
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services =>
        {
            services.AddSingleton<IExpectedEarningsSource>(new AnsweringSource(Figures(runs: 25), new SharedEarningsAttempt(null, OwnRunsReason.NotSharing)));
            services.AddSingleton<IEarningsScreens>(screens);
        });
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.ApplySignatureAsync("AAA-001", "Combat Site", "Angel Sanctum", [new SdeSite(2311, "Angel Sanctum", 1, null, null, null, null, null, false, [])]);
        ActivityWindow window = Open(model);

        Border panel = Find<Border>(window, "ExpectedEarningsPanel");
        TextBlock basis = Find<TextBlock>(window, "BasisLine");
        Button details = Find<Button>(window, "DetailsButton");
        Button sharing = Find<Button>(window, "TurnOnSharingLink");
        Assert.True(details.IsVisible);
        Assert.True(sharing.IsVisible);
        Assert.Equal("updated just now", model.ExpectedEarnings!.UpdatedText);
        Assert.Contains(basis.Inlines!.OfType<Avalonia.Controls.Documents.Run>(), run => run.Text == "updated just now");
        Assert.True(Left(basis, window) >= Left(panel, window) + 13.5);
        Assert.True(Left(details, window) + details.Bounds.Width <= Left(panel, window) + panel.Bounds.Width - 13.5);
        Assert.True(Left(basis, window) + basis.Bounds.Width <= Left(details, window) + 0.5);
        Assert.True(Top(basis, window) + basis.Bounds.Height <= Top(panel, window) + panel.Bounds.Height);
        Assert.NotEqual(DimInk, Assert.IsAssignableFrom<ISolidColorBrush>(sharing.Foreground).Color);

        Point onText = Inside<TextBlock>(window, "TypicalFigure").TranslatePoint(new Point(4, 4), window)!.Value;
        window.MouseDown(onText, MouseButton.Left);
        window.MouseUp(onText, MouseButton.Left);
        Assert.Single(screens.Opened);

        Point onSharing = sharing.TranslatePoint(new Point(sharing.Bounds.Width / 2, sharing.Bounds.Height / 2), window)!.Value;
        window.MouseDown(onSharing, MouseButton.Left);
        window.MouseUp(onSharing, MouseButton.Left);
        Assert.Single(screens.Opened);
        Assert.Equal(1, screens.SharingSettingsOpened);

        details.Command!.Execute(null);
        Assert.Equal(2, screens.Opened.Count);
    }

    /// <summary>ET-524: the marker label sits in its own row above the bar and the worst and best labels in one row under it, so
    /// the label can never run into them, wherever the marker is and whichever way the window is sized. The worst, "log scale" and
    /// best labels also stay apart from each other and inside the panel. The marker label carries this run's ISK and the
    /// strip reads in the readable ink.</summary>
    [AvaloniaTheory]
    [InlineData(560, 30_000_000)]
    [InlineData(560, 1_000_000)]
    [InlineData(560, 900_000_000)]
    [InlineData(380, 30_000_000)]
    [InlineData(380, 900_000_000)]
    [InlineData(1303, 30_000_000)]
    public async Task TheStrip_KeepsItsLabelsApart_AtEveryWidth(double width, int runIsk)
    {
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services =>
            services.AddSingleton<IExpectedEarningsSource>(new FixedSource(Figures(runs: 25))));
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.ApplySignatureAsync("AAA-001", "Combat Site", "Angel Sanctum", [new SdeSite(2311, "Angel Sanctum", 1, null, null, null, null, null, false, [])]);
        ActivityWindow window = Open(model, width, 760);
        model.ExpectedEarnings!.UpdateCurrentIsk(runIsk);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Border panel = Find<Border>(window, "ExpectedEarningsPanel");
        Control marker = Inside<Control>(window, "MarkerLabel");
        TextBlock markerText = Inside<TextBlock>(window, "MarkerText");
        EarningsBandBar bar = Inside<EarningsBandBar>(window, "BandBar");
        TextBlock worst = Inside<TextBlock>(window, "WorstLabel");
        TextBlock best = Inside<TextBlock>(window, "BestLabel");
        TextBlock logScale = window.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "log scale");

        Rect markerRect = Rect(markerText, window);
        Rect barRect = Rect(bar, window);
        Rect worstRect = Rect(worst, window);
        Rect bestRect = Rect(best, window);
        Rect logRect = Rect(logScale, window);
        Rect panelRect = Rect(panel, window);

        Assert.Contains("This run", markerText.Text);
        Assert.True(markerRect.Bottom <= barRect.Top + 0.5, $"label {markerRect} overlaps the bar {barRect}");
        Assert.True(barRect.Bottom <= worstRect.Top + 0.5 && barRect.Bottom <= bestRect.Top + 0.5);
        Assert.False(markerRect.Intersects(worstRect) || markerRect.Intersects(bestRect), $"{markerRect} meets {worstRect} or {bestRect}");
        Assert.True(markerRect.Left >= Left(bar, window) - 0.5 && markerRect.Right <= barRect.Right + 0.5, $"label {markerRect} is off the bar {barRect}");
        Assert.True(worstRect.Right <= logRect.Left + 0.5 && logRect.Right <= bestRect.Left + 0.5,
            $"worst {worstRect}, log scale {logRect} and best {bestRect} overlap");
        Assert.True(worstRect.Left >= panelRect.Left && bestRect.Right <= panelRect.Right, "worst or best runs past the panel");
        Assert.Equal(worstRect.Top, bestRect.Top, 0.5);
        Assert.False(string.IsNullOrWhiteSpace(worst.Text) || string.IsNullOrWhiteSpace(best.Text));
        Assert.True(marker.IsVisible);
    }

    /// <summary>Counter-proof for the label row: with no run on the bar there is no label row, so the strip is shorter.</summary>
    [AvaloniaFact]
    public async Task WithoutARunOnTheBar_ThereIsNoMarkerRow()
    {
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services =>
            services.AddSingleton<IExpectedEarningsSource>(new FixedSource(Figures(runs: 25))));
        ActivityWindowViewModel model = await harness.OpenAsync();
        await model.ApplySignatureAsync("AAA-001", "Combat Site", "Angel Sanctum", [new SdeSite(2311, "Angel Sanctum", 1, null, null, null, null, null, false, [])]);
        ActivityWindow window = Open(model);

        Assert.False(Inside<Control>(window, "MarkerLabel").IsVisible);
        Assert.False(model.ExpectedEarnings!.HasCurrent);
    }

    private static ActivityWindow Open(ActivityWindowViewModel model) => Open(model, 560, 760);

    private static ActivityWindow Open(ActivityWindowViewModel model, double width, double height)
    {
        var window = new ActivityWindow(model);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Width = width;
        window.Height = height;
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(ActivityWindow window, string name) where T : Control =>
        window.FindControl<T>(name) ?? throw new InvalidOperationException($"{name} was not rendered");

    // The strip is a control of its own: its names are in its own scope, so the window's FindControl does not reach them.
    private static T Inside<T>(ActivityWindow window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(control => control.Name == name);

    private static Rect Rect(Visual visual, Visual relativeTo) =>
        new(visual.TranslatePoint(new Point(0, 0), relativeTo) ?? throw new InvalidOperationException("Not in the same tree."), visual.Bounds.Size);

    private static double Top(Visual visual, Visual relativeTo) =>
        visual.TranslatePoint(new Point(0, 0), relativeTo)?.Y ?? throw new InvalidOperationException("Not in the same tree.");

    private static double Left(Visual visual, Visual relativeTo) =>
        visual.TranslatePoint(new Point(0, 0), relativeTo)?.X ?? throw new InvalidOperationException("Not in the same tree.");

    private static ExpectedEarnings Figures(int runs)
    {
        EarningsSample[] samples = [.. Enumerable.Range(0, runs).Select(index => new EarningsSample
        {
            PilotBucket = $"pilot-{index}",
            CompletedAtUtc = DateTime.UtcNow.AddDays(-1 - index),
            DurationSeconds = 900,
            FixedIsk = 10_000_000m * (1 + index % 9),
            LootBasis = LootBasis.CargoDifference
        })];
        return new ExpectedEarnings(EarningsBasis.YourRuns,
            EarningsStatistics.Compute(samples, _ => null, EarningsPolicy.For(ActivityStatsKind.Site), DateTime.UtcNow), 1);
    }

    private sealed class MemoryPicks : ISharedServerPicks
    {
        private string? _address;

        public Task<string?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_address);

        public Task SetAsync(string serverAddress, CancellationToken cancellationToken = default)
        {
            _address = serverAddress;
            return Task.CompletedTask;
        }
    }

    private sealed class AnsweringSource(ExpectedEarnings own, SharedEarningsAttempt shared) : IExpectedEarningsSource
    {
        public Task<ExpectedEarnings> LoadOwnAsync(ActivityStatsKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult(own);

        public Task<SharedEarningsAttempt> LoadSharedAsync(ActivityStatsKey key, int characterId, CancellationToken cancellationToken = default,
            string? activityRef = null, string? onlyServer = null) => Task.FromResult(shared);
    }

    private sealed class FixedSource(ExpectedEarnings own) : IExpectedEarningsSource
    {
        public Task<ExpectedEarnings> LoadOwnAsync(ActivityStatsKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult(own);

        public Task<SharedEarningsAttempt> LoadSharedAsync(ActivityStatsKey key, int characterId, CancellationToken cancellationToken = default, string? activityRef = null, string? onlyServer = null) =>
            Task.FromResult(new SharedEarningsAttempt(null, OwnRunsReason.None));
    }
}
