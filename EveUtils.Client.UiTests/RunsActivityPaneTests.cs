using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-291: the selected run in a pane beside the list, or in a drawer over it. What is pinned here is what the ticket
/// worked out rather than what a render shows — the ways out of the drawer, how the arrows step, and the one case a
/// <c>ListBox</c> cannot hold on to by itself: the selected row's day being folded (ET-290), which takes the row out
/// of the flat sequence and makes the list drop its own selection.
/// </summary>
public sealed class RunsActivityPaneTests
{
    /// <summary>Midday UTC on purpose: the days below group on LOCAL midnight (ET-98), and an evening start plus a
    /// couple of hours would quietly become a fourth day in a zone ahead of UTC.</summary>
    private static readonly DateTime Evening = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<Character> Crew =
    [
        new("Ra Vinter", 90000001), new("Kav Orn", 90000002), new("Deio Tarn", 90000003)
    ];

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    private sealed record Presented(Window Root, Control Content, RunsOverviewViewModel ViewModel);

    /// <summary>ET-291 AC-3: ✕, a click on the scrim and Esc all close the drawer, the selection stays where it was,
    /// and ↵ opens it again. Counter-proof: close by clearing the selection — the usual shortcut — and the second
    /// row of each pair goes red, because reopening would then have nothing to show.</summary>
    [AvaloniaFact]
    public async Task TheDrawerCloses_OnTheCross_OnTheScrim_AndOnEscape_WithTheSelectionStanding()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        await _SaveRunAsync(instance, 90000001, Evening, cancellationToken);

        Presented presented = await _PresentAsync(instance, 758, cancellationToken);
        ActivityOverviewRowViewModel row = _Rows(presented.ViewModel).Single();

        presented.ViewModel.Select(row);
        Assert.True(presented.ViewModel.IsDrawerOpen);
        Assert.Same(row, presented.ViewModel.Pane.Row);

        // ✕
        Button close = _Named<Button>(presented, "DrawerClose");
        close.Command!.Execute(null);
        Assert.False(presented.ViewModel.IsDrawerOpen);
        Assert.Same(row, presented.ViewModel.SelectedRow);

        await presented.ViewModel.OpenSelectedDetailAsync();
        Assert.True(presented.ViewModel.IsDrawerOpen);

        // The scrim over the dimmed list — clicking the list you can still see is the way out.
        Button scrim = _Named<Button>(presented, "ScrimClose");
        scrim.Command!.Execute(null);
        Assert.False(presented.ViewModel.IsDrawerOpen);
        Assert.Same(row, presented.ViewModel.SelectedRow);

        await presented.ViewModel.OpenSelectedDetailAsync();
        Assert.True(presented.ViewModel.IsDrawerOpen);

        // Esc, through the screen's own handler rather than a shortcut binding: a bare Esc can be bound to something
        // else in Settings (ET-209), and a docked tab hears no key at all unless the focus is inside it — which is
        // why opening the drawer moves it there.
        Dispatcher.UIThread.RunJobs();
        presented.Root.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, keySymbol: null);
        Assert.False(presented.ViewModel.IsDrawerOpen);
        Assert.Same(row, presented.ViewModel.SelectedRow);
        Assert.Same(row, presented.ViewModel.Pane.Row);

        // Shut, ↵ opens it again rather than jumping straight to the whole detail screen.
        await presented.ViewModel.OpenSelectedDetailAsync();
        Assert.True(presented.ViewModel.IsDrawerOpen);
    }

    /// <summary>ET-291 AC-4 and the 15 September decision: ↑↓ step over activity rows only — never a pilot's run and
    /// never a day header — and step OVER a folded day without unfolding it. Counter-proof: walk the list's own flat
    /// <c>Items</c> and the first ↓ lands on a sub-row; unfold as you go and the folded day's own assertion goes
    /// red.</summary>
    [AvaloniaFact]
    public async Task Arrows_StepOverSubRowsDayHeaders_AndFoldedDays()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        // Three days. The 13th holds two runs, of which the first is a group of two pilots and so unfolds into
        // sub-rows; the 12th is the day that gets folded; the 11th is where a step from the 13th has to land.
        await _SaveRunAsync(instance, 90000001, Evening, cancellationToken, groupCode: "g-1");
        await _SaveRunAsync(instance, 90000002, Evening.AddMinutes(1), cancellationToken, groupCode: "g-1");
        await _SaveRunAsync(instance, 90000001, Evening.AddHours(2), cancellationToken);
        await _SaveRunAsync(instance, 90000001, Evening.AddDays(-1), cancellationToken);
        await _SaveRunAsync(instance, 90000001, Evening.AddDays(-2), cancellationToken);

        Presented presented = await _PresentAsync(instance, 1303, cancellationToken);
        RunsOverviewViewModel viewModel = presented.ViewModel;
        RunsTabViewModel tab = viewModel.Tabs[0];
        Assert.Equal(3, tab.Days.Count);
        foreach (RunsDayViewModel day in tab.Days)
            day.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();

        // Newest day first, and its rows newest first — the order the list draws.
        RunsDayViewModel newest = tab.Days[0], middle = tab.Days[1], oldest = tab.Days[2];
        ActivityOverviewRowViewModel grouped = newest.Rows.Single(candidate => candidate.HasCrewStack);

        // The grouped row unfolded: its pilots' runs are in the list, and ↓ must not stop on one of them.
        await grouped.ToggleCommand.ExecuteAsync(null);
        await ActivityWindowHarness.WaitUntil(() => grouped.SubRuns.Count > 0);
        Assert.Equal(2, grouped.SubRuns.Count);

        viewModel.Select(newest.Rows[0]);
        viewModel.MoveSelection(1);
        Assert.Same(newest.Rows[1], viewModel.SelectedRow);

        // Past the last row of the newest day: the next day's first activity row, not its header.
        viewModel.MoveSelection(1);
        Assert.Same(middle.Rows[0], viewModel.SelectedRow);

        // Now fold the middle day and step from the newest day's last row again: the fold is stepped over, and the
        // folded day is still folded afterwards.
        viewModel.Select(newest.Rows[^1]);
        middle.IsExpanded = false;
        Dispatcher.UIThread.RunJobs();

        viewModel.MoveSelection(1);
        Assert.Same(oldest.Rows[0], viewModel.SelectedRow);
        Assert.False(middle.IsExpanded);

        // And back up over it the same way.
        viewModel.MoveSelection(-1);
        Assert.Same(newest.Rows[^1], viewModel.SelectedRow);
        Assert.False(middle.IsExpanded);
    }

    /// <summary>ET-291's own pitfall, and the 15 September decision: folding the selected row's day keeps the
    /// selection, keeps the pane on that run and leaves the drawer open — a folded day is not "out of view".
    /// Counter-proof: let <c>ListBox.SelectedItem</c> be the selection. The fold takes the row out of the tab's flat
    /// <c>Items</c>, the list writes null back, and all four assertions go red at once.</summary>
    [AvaloniaFact]
    public async Task FoldingTheSelectedRowsDay_KeepsTheSelection_ThePane_AndTheDrawer()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        await _SaveRunAsync(instance, 90000001, Evening, cancellationToken);
        await _SaveRunAsync(instance, 90000001, Evening.AddDays(-1), cancellationToken);

        Presented presented = await _PresentAsync(instance, 758, cancellationToken);
        RunsOverviewViewModel viewModel = presented.ViewModel;
        RunsTabViewModel tab = viewModel.Tabs[0];
        foreach (RunsDayViewModel any in tab.Days)
            any.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();

        RunsDayViewModel day = tab.Days[0];
        ActivityOverviewRowViewModel row = day.Rows[0];
        viewModel.Select(row);
        ListBox list = _Named<ListBox>(presented, "ActivityList");
        Dispatcher.UIThread.RunJobs();
        presented.Root.UpdateLayout();
        Assert.Same(row, list.SelectedItem);

        day.IsExpanded = false;
        Dispatcher.UIThread.RunJobs();
        presented.Root.UpdateLayout();

        Assert.Same(row, viewModel.SelectedRow);
        Assert.Same(row, viewModel.Pane.Row);
        Assert.True(viewModel.IsDrawerOpen);
        // The list has lost sight of it, and says so — which is exactly the null that must not be believed.
        Assert.Null(list.SelectedItem);

        // ↓ from a selection inside a folded day goes to the next unfolded day's first row.
        viewModel.MoveSelection(1);
        Assert.Same(tab.Days[1].Rows[0], viewModel.SelectedRow);

        // And the row comes back to the list's own selection when its day is unfolded again.
        viewModel.Select(row);
        day.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        presented.Root.UpdateLayout();
        Assert.Same(row, list.SelectedItem);
    }

    /// <summary>ET-291 AC-7: 1199 is narrow and 1200 is wide, read off the content root's own bounds — the module
    /// host's column when docked, the window when floating. Counter-proof: size off the window and the docked case
    /// answers for a width it was never given.</summary>
    [AvaloniaFact]
    public async Task TheBreakpoint_IsWideAt1200_AndNarrowAt1199()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        await _SaveRunAsync(instance, 90000001, Evening, cancellationToken);

        foreach ((double width, bool wide) in new[] { (758d, false), (1199d, false), (1200d, true), (1303d, true) })
        {
            Presented presented = await _PresentAsync(instance, width, cancellationToken);
            Assert.Equal(wide, presented.ViewModel.IsWide);

            Border paneHost = _Named<Border>(presented, "PaneHost");
            Border drawer = _Named<Border>(presented, "Drawer");
            Assert.Equal(wide, paneHost.IsVisible);
            Assert.Equal(!wide, drawer.IsVisible);
            Assert.Equal(wide ? RunsLayout.PaneWidth : 0, paneHost.Bounds.Width);
            // No horizontal overflow at any of the four: the list gives the pane its column and keeps the rest.
            ListBox list = _Named<ListBox>(presented, "ActivityList");
            Assert.Equal(width - (wide ? RunsLayout.PaneWidth : 0), list.Bounds.Width);
            Assert.True(presented.Content.Bounds.Width <= width + 0.5);

            presented.Root.Content = null;
            presented.Root.Close();
        }
    }

    /// <summary>ET-291 AC-1 on the narrow layout: the drawer is exactly 430 px, right-aligned, and lies over the
    /// whole module content — header and bands included — with the scrim over the rest. Counter-proof: put the drawer
    /// inside the <c>DockPanel</c> beside the list (ET-285's two-undocked-children trap) and it starts below the
    /// month bar instead of at the top.</summary>
    [AvaloniaFact]
    public async Task TheDrawer_Is430Wide_RightAligned_AndOverTheWholeContent()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        await _SaveRunAsync(instance, 90000001, Evening, cancellationToken);

        Presented presented = await _PresentAsync(instance, 758, cancellationToken);
        presented.ViewModel.Select(_Rows(presented.ViewModel).Single());
        Dispatcher.UIThread.RunJobs();
        presented.Root.UpdateLayout();

        Border drawer = _Named<Border>(presented, "Drawer");
        Border scrim = _Named<Border>(presented, "Scrim");
        Assert.Equal(RunsLayout.DrawerWidth, drawer.Bounds.Width);
        Assert.Equal(758 - RunsLayout.DrawerWidth, drawer.Bounds.X);
        Assert.Equal(0, drawer.Bounds.Y);
        Assert.Equal(presented.Content.Bounds.Height, drawer.Bounds.Height);
        Assert.Equal(presented.Content.Bounds.Width, scrim.Bounds.Width);
    }

    private static IReadOnlyList<ActivityOverviewRowViewModel> _Rows(RunsOverviewViewModel viewModel) =>
        [.. viewModel.Tabs[0].Days.SelectMany(day => day.Rows)];

    private static T _Named<T>(Presented presented, string name) where T : Control =>
        presented.Content.GetVisualDescendants().OfType<T>().First(control => control.Name == name);

    private static async Task _SaveRunAsync(TestClientInstance instance, long characterId, DateTime startedAtUtc,
        CancellationToken cancellationToken, string? groupCode = null)
    {
        var dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, startedAtUtc,
            1234, "Homefront", 30000142, groupCode), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAtUtc.AddMinutes(15),
            startedAtUtc.AddMinutes(16), [], [], [], []), cancellationToken);
    }

    /// <summary>The control tree as the operator sees it docked: the host lifts the window's content out and
    /// re-parents it, so everything here runs against that content rather than a window that is never shown.</summary>
    private static async Task<Presented> _PresentAsync(
        TestClientInstance instance, double width, CancellationToken cancellationToken)
    {
        var dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        // No lane clock, and no wait before the pane reads: the window that would dispose the view model is never
        // closed, and a test should not sleep through a debounce meant for a held-down arrow key.
        var viewModel = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services,
            Crew, runClock: false, paneReadDelay: TimeSpan.Zero);
        await viewModel.LoadAsync(cancellationToken);

        var window = new RunsWindow(viewModel) { Width = width, Height = 1400 };
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
        Dispatcher.UIThread.RunJobs();
        return new Presented(root, content, viewModel);
    }
}
