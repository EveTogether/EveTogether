using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Controls.Map;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-401: Fleet metrics below the breakpoint stacks summary, map, list instead of putting the map beside them, and
/// nothing in the summary, the commander badge or the density hint overlaps or leaves its box. Measured as bounds,
/// not looked at.
/// </summary>
public sealed class FleetMetricsStackedLayoutTests
{
    private const int Commander = 90250177;
    private const int Member = 90250178;

    [AvaloniaTheory]
    [InlineData(700)]
    [InlineData(860)]
    public async Task BelowTheBreakpoint_StacksSummaryMapList_AndNothingOverlapsOrLeavesItsBox(double width)
    {
        using var screen = await Screen.OpenAsync(width);

        Rect summary = screen.BoundsOf("SummaryCard");
        Rect map = screen.BoundsOf("MapCardHost");
        Rect list = screen.BoundsOf("MemberList");

        Assert.True(screen.Metrics.IsStacked);
        Assert.True(summary.Bottom <= map.Top, $"summary {summary} / map {map}");
        Assert.True(map.Bottom <= list.Top, $"map {map} / list {list}");
        Assert.Equal(summary.Left, map.Left);
        Assert.True(map.Width >= screen.Root.Bounds.Width - 2 * 14 - 1, $"map {map} in {screen.Root.Bounds}");
        Assert.True(map.Height >= 240, $"map {map}");

        foreach (Rect text in screen.TextWithin("SummaryFigures"))
            Assert.True(summary.Contains(text), $"summary text {text} leaves the summary card {summary}");

        Rect badge = screen.BoundsOf("CommanderBadge");
        Assert.True(summary.Contains(badge), $"badge {badge} leaves the summary card {summary}");
        foreach (Rect figure in screen.ChildrenOf("SummaryFigures"))
            Assert.False(badge.Intersects(figure), $"badge {badge} overlaps figure {figure}");

        Rect hint = screen.BoundsOf("LayoutHintText");
        Rect bar = screen.BoundsOf("ViewBar");
        Assert.True(screen.Metrics.HintOnOwnRow);
        Assert.True(hint.Width >= bar.Width - 4, $"hint {hint} is not the bar's full width {bar}");
        foreach (Rect button in screen.ChildrenOf("ViewBar").Where(child => child != hint))
        {
            Assert.False(hint.Intersects(button), $"hint {hint} overlaps {button}");
            Assert.True(hint.Bottom <= button.Top, $"hint {hint} is not above {button}");
        }
    }

    [AvaloniaFact]
    public async Task AtTheDesignWidth_TheMapStaysBesideSummaryAndList_AndTheHintBesideTheButtons()
    {
        using var screen = await Screen.OpenAsync(1280);

        Rect summary = screen.BoundsOf("SummaryCard");
        Rect map = screen.BoundsOf("MapCardHost");
        Rect list = screen.BoundsOf("MemberList");

        Assert.False(screen.Metrics.IsStacked);
        Assert.False(screen.Metrics.HintOnOwnRow);
        Assert.Equal(440, map.Width);
        Assert.True(summary.Right <= map.Left && list.Right <= map.Left, $"summary {summary}, list {list}, map {map}");
        Assert.Equal(summary.Top, map.Top);

        Rect hint = screen.BoundsOf("LayoutHintText");
        foreach (Rect button in screen.ChildrenOf("ViewBar").Where(child => child != hint))
        {
            Assert.False(hint.Intersects(button), $"hint {hint} overlaps {button}");
            Assert.True(hint.Right <= button.Left, $"hint {hint} is not beside {button}");
        }
    }

    /// <summary>
    /// The swap must not rebuild the map (same card, same StarMapControl) and must not flicker at the edge: each way the
    /// switch happens at its own width, with the hysteresis band in between.
    /// </summary>
    [AvaloniaFact]
    public async Task Resizing_ThroughTheBreakpoint_KeepsTheSameMapAndSwitchesOnceEachWay()
    {
        using var screen = await Screen.OpenAsync(1280);
        FleetMapCard card = screen.Window.FindControl<FleetMapCard>("MapCard") ?? throw new InvalidOperationException("no card");
        StarMapControl map = card.FindControl<StarMapControl>("CardMap") ?? throw new InvalidOperationException("no map");
        double below = FleetMetricsWidth.StackBelow;

        var states = new List<bool>();
        foreach (double contentWidth in new[] { below + 40, below + 5, below - 1, below + 5, below + 19, below + 21, below + 5, below - 1 })
        {
            screen.SetContentWidth(contentWidth);
            states.Add(screen.Metrics.IsStacked);
        }

        Assert.Equal([false, false, true, true, true, false, false, true], states);
        Assert.Same(card, screen.Window.FindControl<FleetMapCard>("MapCard"));
        Assert.Same(map, card.FindControl<StarMapControl>("CardMap"));
        Assert.Same(screen.Metrics.FleetMap, card.DataContext);
        Assert.True(map.IsAttachedToVisualTree());
    }

    private sealed class Screen : IDisposable
    {
        private readonly TestClientInstance _instance;

        private Screen(TestClientInstance instance, FleetMetricsViewModel metrics, FleetMetricsWindow window)
        {
            _instance = instance;
            Metrics = metrics;
            Window = window;
        }

        public FleetMetricsViewModel Metrics { get; }

        public FleetMetricsWindow Window { get; }

        public Control Root => Window.FindControl<Grid>("ContentRoot") ?? throw new InvalidOperationException("no content root");

        public static async Task<Screen> OpenAsync(double width)
        {
            TestClientInstance instance = TestClientInstance.Create(services =>
            {
                services.AddSingleton<ISdeAccessor>(MapFixture.Sde());
                services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
                {
                    [Commander] = "RaymondKrah",
                    [Member] = "Lionear",
                });
            });
            var roster = new FakeFleetClient
            {
                Members =
                [
                    new FleetMemberInfo(1, Commander, -1, -1, FleetRole.FleetCommander, false),
                    new FleetMemberInfo(2, Member, 1, 1, FleetRole.SquadMember, false),
                ],
            };
            var info = new FleetInfo(100, "Op", null, FleetVisibility.Public, FleetState.Active, 1, null, null,
                DateTimeOffset.UnixEpoch, FleetActivation.Active);
            var metrics = new FleetMetricsViewModel(instance.Services, roster, info, Commander);
            for (var i = 0; i < 100 && metrics.Members.Count < 2; i++)
                await Task.Delay(20);

            // The widest things the summary ever shows, so a label that only fits while the values are dashes cannot pass.
            metrics.DealtTotal = "1,234 dps";
            metrics.ReceivedTotal = "2,345 dps";
            metrics.RepsOutTotal = "59 hp/s";
            metrics.NeutedMembers = "Lionear, Tarek, Kaelen";
            metrics.FleetApplication = "83%";
            metrics.BountyTotal = "999.99B ISK";

            var window = new FleetMetricsWindow(metrics) { Width = width, Height = 900 };
            window.Show();
            var screen = new Screen(instance, metrics, window);
            screen.Settle();
            return screen;
        }

        public void SetContentWidth(double contentWidth)
        {
            // The window adds its own frame around the content; set the window so the content root ends up this wide.
            double frame = Window.Bounds.Width - Root.Bounds.Width;
            Window.Width = contentWidth + frame;
            Settle();
        }

        public void Settle()
        {
            for (var pass = 0; pass < 3; pass++)
            {
                Dispatcher.UIThread.RunJobs();
                Window.UpdateLayout();
            }
        }

        public Rect BoundsOf(string name)
        {
            Control control = Window.FindControl<Control>(name) ?? throw new InvalidOperationException($"no {name}");
            return InRoot(control);
        }

        public IEnumerable<Rect> ChildrenOf(string name)
        {
            Control parent = Window.FindControl<Control>(name) ?? throw new InvalidOperationException($"no {name}");
            return parent.GetVisualChildren().OfType<Control>().Where(child => child.IsVisible).Select(InRoot).ToList();
        }

        public IEnumerable<Rect> TextWithin(string name)
        {
            Control parent = Window.FindControl<Control>(name) ?? throw new InvalidOperationException($"no {name}");
            return parent.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsVisible).Select(InRoot).ToList();
        }

        private Rect InRoot(Control control) =>
            new(control.TranslatePoint(default, Root) ?? throw new InvalidOperationException("detached"), control.Bounds.Size);

        public void Dispose()
        {
            Window.Close();
            Metrics.Dispose();
            _instance.Dispose();
        }
    }
}
