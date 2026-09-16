using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Controls;
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
/// ET-304: the day header's totals, read off <c>.Bounds</c>. Jithran's screenshot showed the source bar overlapping
/// the ISK figure and its text clipped ("ISK n_", "ISK _") on every day that still carried a "n local ↑" marker, and
/// every day's summary starting at its own x position ("4 activities" against "46 activities") instead of lining up.
/// </summary>
public sealed class RunsDayHeaderColumnsTests
{
    private static readonly DateTime Evening = new(2026, 9, 13, 20, 0, 0, DateTimeKind.Utc);

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    private sealed record Presented(Window Root, Control Content, RunsOverviewViewModel ViewModel);

    /// <summary>Pure arithmetic, no controls: which columns <see cref="RunsLayout.DayHeader"/> keeps for a width —
    /// flown time first, then the source bar, exactly as the day header's totals give way (ET-304).</summary>
    [Theory]
    [InlineData(1920d, DayHeaderTier.Full)]
    [InlineData(696d, DayHeaderTier.Full)]
    [InlineData(695d, DayHeaderTier.NoFlown)]
    [InlineData(600d, DayHeaderTier.NoFlown)]
    [InlineData(599d, DayHeaderTier.NoBar)]
    [InlineData(420d, DayHeaderTier.NoBar)]
    public void DayHeader_PicksTheFirstTierThatFits(double width, DayHeaderTier expected) =>
        Assert.Equal(expected, RunsLayout.DayHeader(width));

    /// <summary>No two pieces of any realised day header share a pixel, at every width the ticket names, with and
    /// without a "n local ↑" marker to make room for. Counter-proof: before ET-304 the marker's column was Auto and
    /// collapsed to nothing when the button was hidden, so a day that had one and a day that did not handed the
    /// header two different widths — the marker column growing in is exactly what pushed the source bar over the
    /// ISK figure.</summary>
    [AvaloniaTheory]
    [InlineData(1920d, false)]
    [InlineData(1920d, true)]
    [InlineData(1400d, false)]
    [InlineData(1400d, true)]
    [InlineData(1200d, false)]
    [InlineData(1200d, true)]
    [InlineData(975d, false)]
    [InlineData(975d, true)]
    [InlineData(800d, false)]
    [InlineData(800d, true)]
    [InlineData(630d, false)]
    [InlineData(630d, true)]
    [InlineData(420d, false)]
    [InlineData(420d, true)]
    public async Task DayHeader_NoTwoColumnsOverlap_AndNoTextIsTrimmedPastItsColumn(double width, bool withLocalMarker)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        await _SaveRunAsync(instance, 90000001, Evening, cancellationToken, netIsk: 1_110_000_000m);
        Presented presented = await _PresentAsync(instance, width, cancellationToken);

        RunsDayViewModel day = presented.ViewModel.Tabs[0].Days.Single();
        if (withLocalMarker)
        {
            day.ShowPublishLocal = true;
            day.PublishLocalButtonText = "1 local ↑";
        }
        _Settle(presented);

        Grid totals = _DayTotalsOf(presented, day);
        Rect totalsRect = _Rect(presented, totals);

        List<Control> parts = [.. totals.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsVisible && (c.Classes.Contains("caret") || c.Classes.Contains("dayweek")
                || c.Classes.Contains("daydate") || c.Classes.Contains("daysum") || c is IskSourceBar))];
        Assert.NotEmpty(parts);

        List<Rect> rects = [.. parts.Select(part => _Rect(presented, part))];
        for (int i = 0; i < rects.Count; i++)
        {
            Assert.True(rects[i].Right <= totalsRect.Right + 0.5,
                $"{parts[i].Classes} {rects[i]} runs past the header's own {totalsRect} at {width} (local={withLocalMarker})");
            for (int j = i + 1; j < rects.Count; j++)
                Assert.False(rects[i].Intersects(rects[j]),
                    $"{parts[i].Classes} {rects[i]} overlaps {parts[j].Classes} {rects[j]} at {width} (local={withLocalMarker})");
        }

        // The publish column is reserved whether the button shows or not (ET-304 AC-1): the totals Grid's own right
        // edge sits exactly RunsLayout.DayLocalWidth short of the list's own right edge regardless — an Auto column
        // there (the pre-ET-304 shape) would instead put it right against the list when the button is hidden and
        // pull it in by the button's own width the moment one appears, which is what let the totals run into the
        // marker. The list, not the content root: at 1200 px and up the pane still takes its own 400 px column.
        Border header = (Border)totals.GetVisualParent()!;
        Rect headerRect = _Rect(presented, header);
        Rect listRect = _Rect(presented, _Named<ListBox>(presented, "ActivityList"));
        Assert.True(headerRect.Right <= presented.Content.Bounds.Width + 0.5,
            $"day header {headerRect} runs past the content root's {presented.Content.Bounds.Width} at {width}");
        double expectedRight = listRect.Right - RunsLayout.DayLocalWidth;
        Assert.True(System.Math.Abs(headerRect.Right - expectedRight) <= 0.5,
            $"totals {headerRect} ends at {headerRect.Right}, not the {expectedRight} the reserved publish column implies, at {width} (local={withLocalMarker})");
    }

    /// <summary>The same width handed to two days of very different figures still lines every column up at the same
    /// x — "1 activity"/"2.9k ISK" against "46 activities"/"1.11B ISK net" is the exact contrast Jithran's own
    /// acceptance criteria named. Counter-proof: before ET-304 the header was one right-aligned sentence, so a
    /// shorter sentence started further right than a longer one on the very same day column.</summary>
    [AvaloniaFact]
    public async Task DayHeader_ColumnsShareXPosition_AcrossDaysOfVeryDifferentFigures()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        // One day: a single site worth 2.9k ISK. The other: 46 activities net to 1.11B ISK.
        await _SaveRunAsync(instance, 90000001, Evening, cancellationToken, netIsk: 2_900m);
        for (int i = 0; i < 46; i++)
            await _SaveRunAsync(instance, 90000001, Evening.AddDays(-1).AddMinutes(i), cancellationToken,
                netIsk: 1_110_000_000m / 46);

        Presented presented = await _PresentAsync(instance, 1920, cancellationToken);
        List<RunsDayViewModel> days = [.. presented.ViewModel.Tabs[0].Days];
        Assert.Equal(2, days.Count);
        RunsDayViewModel light = days.Single(day => day.CountText == "1 activity");
        RunsDayViewModel heavy = days.Single(day => day.CountText == "46 activities");
        Assert.Contains("2.9k", light.NetText);
        Assert.Contains("1.11B", heavy.NetText);

        Grid lightTotals = _DayTotalsOf(presented, light);
        Grid heavyTotals = _DayTotalsOf(presented, heavy);

        foreach (string cls in new[] { "caret", "daysum" })
        {
            List<double> lightX = [.. lightTotals.GetVisualDescendants().OfType<Control>()
                .Where(c => c.IsVisible && c.Classes.Contains(cls)).Select(c => _Rect(presented, c).X)];
            List<double> heavyX = [.. heavyTotals.GetVisualDescendants().OfType<Control>()
                .Where(c => c.IsVisible && c.Classes.Contains(cls)).Select(c => _Rect(presented, c).X)];
            Assert.Equal(lightX.Count, heavyX.Count);
            for (int i = 0; i < lightX.Count; i++)
                Assert.True(System.Math.Abs(lightX[i] - heavyX[i]) <= 0.5,
                    $"{cls}[{i}] starts at {lightX[i]} for the light day but {heavyX[i]} for the heavy one");
        }

        // The bar's own column, specifically: same x regardless of which day's Isk breakdown it draws.
        var lightBar = lightTotals.GetVisualDescendants().OfType<IskSourceBar>().Single();
        var heavyBar = heavyTotals.GetVisualDescendants().OfType<IskSourceBar>().Single();
        Assert.True(System.Math.Abs(_Rect(presented, lightBar).X - _Rect(presented, heavyBar).X) <= 0.5,
            $"the source bar starts at {_Rect(presented, lightBar).X} for the light day but {_Rect(presented, heavyBar).X} for the heavy one");
    }

    private static Grid _DayTotalsOf(Presented presented, RunsDayViewModel day)
    {
        var list = _Named<ListBox>(presented, "ActivityList");
        Control container = list.ContainerFromItem(day) ?? throw new Xunit.Sdk.XunitException("day header not realised");
        return container.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "DayTotals");
    }

    private static Rect _Rect(Presented presented, Control control)
    {
        Point origin = control.TranslatePoint(new Point(0, 0), presented.Content) ?? default;
        return new Rect(origin, control.Bounds.Size);
    }

    private static T _Named<T>(Presented presented, string name) where T : Control =>
        presented.Content.GetVisualDescendants().OfType<T>().First(control => control.Name == name);

    private static void _Settle(Presented presented)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            presented.Root.UpdateLayout();
        }
    }

    private static async Task _SaveRunAsync(TestClientInstance instance, long characterId, DateTime startedAtUtc,
        CancellationToken cancellationToken, decimal netIsk)
    {
        var dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, startedAtUtc,
            1234, "Homefront", 30000142, null), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15),
            startedAtUtc.AddMinutes(16), [], [new RunBountyEntryInput { OccurredAtUtc = startedAtUtc.AddMinutes(5), Isk = netIsk }],
            [], []), cancellationToken);
    }

    private static async Task<Presented> _PresentAsync(
        TestClientInstance instance, double width, CancellationToken cancellationToken, double windowHeight = 1400)
    {
        var dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        var viewModel = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services,
            [new Character("Jithran", 90000001)], runClock: false, paneReadDelay: TimeSpan.Zero);
        await viewModel.LoadAsync(cancellationToken);
        foreach (RunsDayViewModel day in viewModel.Tabs[0].Days)
            day.IsExpanded = false;

        var window = new RunsWindow(viewModel) { Width = width, Height = windowHeight };
        var display = new FakeDisplay();
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(window, "RUNS", "runs", "runs");

        var content = (Control)Assert.Single(display.HostTabs).Content!;
        var root = new Window { Width = width, Height = windowHeight, Content = content };
        root.Show();
        var presented = new Presented(root, content, viewModel);
        _Settle(presented);
        return presented;
    }
}
