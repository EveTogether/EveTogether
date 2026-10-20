using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
        EarningsBandBar bar = Find<EarningsBandBar>(window, "BandBar");

        Assert.True(panel.IsVisible);
        Assert.True(body.IsVisible);
        Assert.True(model.ExpectedEarnings!.IsExpanded);
        Assert.True(Top(clock, window) < Top(panel, window));
        Assert.True(Top(panel, window) + panel.Bounds.Height <= Top(sections, window) + 0.5);
        Assert.Equal(EarningsBandBar.BarHeight, bar.Bounds.Height);
        Assert.True(bar.Bounds.Width > 150, $"bar is {bar.Bounds.Width} wide");
        Assert.InRange(bar.Bounds.Width, panel.Bounds.Width - 28.5, panel.Bounds.Width - 27.5);
        Assert.False(string.IsNullOrWhiteSpace(Find<TextBlock>(window, "TypicalFigure").Text));
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

        Assert.False(Find<StackPanel>(window, "ExpectedEarningsFigures").IsVisible);
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

    private static ActivityWindow Open(ActivityWindowViewModel model)
    {
        var window = new ActivityWindow(model);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Width = 560;
        window.Height = 760;
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(ActivityWindow window, string name) where T : Control =>
        window.FindControl<T>(name) ?? throw new InvalidOperationException($"{name} was not rendered");

    private static double Top(Visual visual, Visual relativeTo) =>
        visual.TranslatePoint(new Point(0, 0), relativeTo)?.Y ?? throw new InvalidOperationException("Not in the same tree.");

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
