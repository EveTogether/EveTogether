using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-84. The rail's launcher stack (FITS..ABOUT) used to be the DockPanel's LastChildFill child, so below 776px
/// (docked) / 827px (floating) it did not shrink or clip — it drew straight over the bottom block (DOCK/FLOAT/HIDE)
/// below it, because the launcher rendered last. At the shipped default of 720 that already covered 57 of DOCK's 59
/// pixels while floating, and DOCK is the only way back to docked mode. IsEffectivelyVisible stays true on every
/// button through all of this, so a visibility assertion cannot see the bug; these check geometry instead. The
/// launcher now scrolls, so a button that's scrolled past the fold is expected to be off-screen — these only hold
/// buttons that are actually painted (fully inside the launcher's own viewport, or the always-visible bottom block)
/// to the "must not overlap, must be clickable at its centre" bar.
/// </summary>
public class MainWindowRailLayoutTests
{
    private static MainWindow Show(double height, bool floating)
    {
        var vm = new MainWindowViewModel { IsFloating = floating };
        var window = new MainWindow { DataContext = vm, Width = floating ? 360 : 1100, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        // RunJobs settles layout, but InputHitTest resolves against the composition tree, which only exists once a
        // render pass has run. Without this the hit tests below depend on a tick landing by luck (ET-149).
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        return window;
    }

    private static Border RailOf(Window window) =>
        window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Rail");

    private static ScrollViewer LauncherScrollerOf(Window window) =>
        window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "RailLauncherScroller");

    private static List<Button> RailButtons(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("railitem")).ToList();

    private static string Label(Button button) => ToolTip.GetTip(button) as string ?? button.Name ?? "rail button";

    private static Rect RectIn(Control control, Visual ancestor) =>
        new(control.TranslatePoint(default, ancestor) ?? throw new InvalidOperationException("control left its ancestor"),
            control.Bounds.Size);

    // A button not inside the launcher's ScrollViewer is the always-visible bottom block (DOCK/CHARS/window
    // controls); one inside it only counts as on-screen while it is fully within the current scroll viewport —
    // that is the whole point of scrolling instead of clipping mid-button.
    private static bool IsOnScreen(Button button, Window window, ScrollViewer scroller, Rect scrollerRectInWindow)
    {
        if (!button.GetVisualAncestors().Contains(scroller)) return true;
        var rect = RectIn(button, window);
        return rect.Top >= scrollerRectInWindow.Top - 0.5 && rect.Bottom <= scrollerRectInWindow.Bottom + 0.5;
    }

    // Docked and floating each carry a different bottom block (129px vs 180px, per the ET-84 grooming), and 400 is
    // short enough that the launcher never fully fits — exactly where the old layout stacked buttons on top of
    // each other instead of scrolling.
    public static IEnumerable<object[]> HeightsAndModes =>
    [
        [720d, false], [400d, false], [720d, true], [400d, true],
    ];

    [AvaloniaTheory]
    [MemberData(nameof(HeightsAndModes))]
    public void OnScreenRailButtons_NeverOverlapEachOther(double height, bool floating)
    {
        var window = Show(height, floating);
        Border rail = RailOf(window);
        ScrollViewer scroller = LauncherScrollerOf(window);
        Rect scrollerRect = RectIn(scroller, window);
        List<Button> onScreen = RailButtons(window).Where(b => IsOnScreen(b, window, scroller, scrollerRect)).ToList();
        Assert.True(onScreen.Count >= 2, "expected at least the always-visible dock/chars toggles on screen");

        var bounds = onScreen.Select(b => RectIn(b, rail)).ToList();
        for (var i = 0; i < bounds.Count; i++)
            for (var j = i + 1; j < bounds.Count; j++)
                Assert.False(bounds[i].Intersects(bounds[j]),
                    $"'{Label(onScreen[i])}' overlaps '{Label(onScreen[j])}' at height {height}, floating={floating}");

        window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(HeightsAndModes))]
    public void OnScreenRailButtons_NeverSpillPastTheRail(double height, bool floating)
    {
        var window = Show(height, floating);
        Border rail = RailOf(window);
        ScrollViewer scroller = LauncherScrollerOf(window);
        Rect scrollerRect = RectIn(scroller, window);

        foreach (var button in RailButtons(window).Where(b => IsOnScreen(b, window, scroller, scrollerRect)))
        {
            var bounds = RectIn(button, rail);
            Assert.True(bounds.Bottom <= rail.Bounds.Height + 0.5,
                $"'{Label(button)}' bottom {bounds.Bottom} is past the rail's own height {rail.Bounds.Height} at {height}, floating={floating}");
        }

        window.Close();
    }

    // Bounds alone only prove on-screen buttons don't stack; a hit-test on each one's own centre is what proves it
    // is actually clickable — the ET-84 grooming calls this out as the only assertion that really tests "reachable".
    [AvaloniaTheory]
    [MemberData(nameof(HeightsAndModes))]
    public void OnScreenRailButtons_AreHitTestableAtTheirOwnCentre(double height, bool floating)
    {
        var window = Show(height, floating);
        ScrollViewer scroller = LauncherScrollerOf(window);
        Rect scrollerRect = RectIn(scroller, window);

        foreach (var button in RailButtons(window).Where(b => IsOnScreen(b, window, scroller, scrollerRect)))
        {
            var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)
                ?? throw new InvalidOperationException($"{Label(button)} left the window");
            var hit = window.InputHitTest(centre) as Visual;
            Assert.True(hit is not null && (ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button)),
                $"a click on '{Label(button)}''s centre does not reach it at height {height}, floating={floating}");
        }

        window.Close();
    }

    // The structural guarantee the fix actually rests on: the launcher's scroll viewport and the fixed bottom
    // block are two disjoint regions of the rail, at every height — not "whatever happens to be visible right now".
    [AvaloniaTheory]
    [MemberData(nameof(HeightsAndModes))]
    public void LauncherViewport_NeverOverlapsTheBottomBlock(double height, bool floating)
    {
        var window = Show(height, floating);
        Border rail = RailOf(window);
        ScrollViewer scroller = LauncherScrollerOf(window);
        StackPanel bottomBlock = rail.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == "RailBottomBlock");

        Rect scrollerRect = RectIn(scroller, rail);
        Rect bottomRect = RectIn(bottomBlock, rail);

        Assert.False(scrollerRect.Intersects(bottomRect),
            $"the launcher viewport {scrollerRect} overlaps the bottom block {bottomRect} at height {height}, floating={floating}");
        Assert.True(bottomRect.Bottom <= rail.Bounds.Height + 0.5,
            $"the bottom block spills past the rail's own height at {height}, floating={floating}");

        window.Close();
    }

    // The specific trap the grooming flagged as Major: floating at the shipped 720 default used to cover 57 of
    // DOCK's 59 pixels, and DOCK is the only way back to docked mode — a dead end reachable out of the box.
    [AvaloniaFact]
    public void DockButton_StaysReachable_WhileFloatingAtTheShippedDefaultHeight()
    {
        var window = Show(720, floating: true);
        Button dock = RailButtons(window).Single(b => Label(b).StartsWith("Dock / undock", StringComparison.Ordinal));

        var centre = dock.TranslatePoint(new Point(dock.Bounds.Width / 2, dock.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("DOCK left the window");
        var hit = window.InputHitTest(centre) as Visual;
        Assert.True(hit is not null && (ReferenceEquals(hit, dock) || hit.GetVisualAncestors().Contains(dock)),
            "DOCK must stay clickable while floating at 720 — it is the only way back to docked mode");

        window.Close();
    }
}
