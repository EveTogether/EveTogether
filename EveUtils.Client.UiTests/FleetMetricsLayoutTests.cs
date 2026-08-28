using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Controls;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Dtos;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The fleet-metrics window trades detail per member for members per screen: list (everything), grid (cards, no
/// cap/neut/bounty figures) and compact (one line, no graph either). The chosen density is a whole-install
/// preference, so it survives closing the window and the client. Whatever the density, the commander-presence badge
/// stays in the header.
/// </summary>
public class FleetMetricsLayoutTests
{
    private const int Commander = 90250177;
    private const int Member = 90250178;
    private const int Latecomer = 90250179;   // not on the roster: turns up through a sample, as a real straggler does
    private const int Stranger = 90250180;    // never in this fleet at all
    private const long FleetId = 100;

    private static readonly FleetInfo Op = new(FleetId, "Op", null, FleetVisibility.Public, FleetState.Active, 1,
        null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active);

    private static TestClientInstance CreateInstance() => TestClientInstance.Create(services =>
        services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
        {
            [Commander] = "RaymondKrah",
            [Member] = "Lionear",
            [Latecomer] = "Tarek",
        }));

    private static FakeFleetClient Roster() => new()
    {
        Members =
        [
            new FleetMemberInfo(1, Commander, -1, -1, FleetRole.FleetCommander, false),
            new FleetMemberInfo(2, Member, 1, 1, FleetRole.SquadMember, false),
        ],
    };

    // Every render assertion needs the roster pre-fill to have landed, otherwise it asserts against an empty list.
    private static async Task<FleetMetricsViewModel> BuildViewModelAsync(TestClientInstance instance, IFleetClient fleets)
    {
        var vm = new FleetMetricsViewModel(instance.Services, fleets, Op);
        for (var i = 0; i < 100 && vm.Members.Count < 2; i++)
            await Task.Delay(20);
        Assert.Equal(2, vm.Members.Count);
        return vm;
    }

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    /// <summary>
    /// The two ways this screen ever reaches a user, both of which every render assertion has to hold for. Docked is
    /// the default and the one that bites: <see cref="ModuleHostService"/> does not show the module's own window, it
    /// lifts <c>window.Content</c> out and reparents it into a tab in the main window — so anything that lives on the
    /// window rather than on its content (its <c>Styles</c>, above all) is left behind.
    /// </summary>
    public enum Shell
    {
        OwnWindow,
        DockedTab
    }

    // A screen on a fleet whose members share a location and a bounty, so every field the list shows has something to
    // show and its absence in a denser layout means the layout dropped it, not that the data was missing.
    private static async Task<(Window Root, FleetMetricsViewModel Vm)> ShowAsync(
        TestClientInstance instance, FleetMetricsLayout layout, Shell shell)
    {
        var vm = await BuildViewModelAsync(instance, Roster());
        var bus = instance.Services.GetRequiredService<IEventBus>();
        await PublishAsync(bus, vm, MetricKind.Location, "Jita");
        await PublishAsync(bus, vm, MetricKind.Bounty, null, 5_000_000);

        vm.SetLayoutCommand.Execute(layout);

        var window = new FleetMetricsWindow(vm) { Width = 900, Height = 620 };
        Window root = window;
        if (shell is Shell.DockedTab)
        {
            var display = new FakeDisplay { IsFloating = false };
            var host = new ModuleHostService();
            host.SetOwner(new Window());
            host.SetHost(display);
            host.Open(window, "FLEET METRICS", "fleet");

            // Stand the reparented content in a plain window: the module's own window is deliberately not the host,
            // which is exactly what this path has to survive.
            root = new Window { Width = 900, Height = 620, Content = Assert.Single(display.HostTabs).Content };
        }

        root.Show();
        Dispatcher.UIThread.RunJobs();
        return (root, vm);
    }

    // Same presentation step for a view-model a test has already set up itself.
    private static async Task<Control> ShowExistingAsync(FleetMetricsViewModel vm, Shell shell)
    {
        var window = new FleetMetricsWindow(vm) { Width = 900, Height = 620 };
        Window root = window;
        if (shell is Shell.DockedTab)
        {
            var display = new FakeDisplay { IsFloating = false };
            var host = new ModuleHostService();
            host.SetOwner(new Window());
            host.SetHost(display);
            host.Open(window, "FLEET METRICS", "fleet");
            root = new Window { Width = 900, Height = 620, Content = Assert.Single(display.HostTabs).Content };
        }

        root.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
        return root;
    }

    private static async Task MoveAsync(TestClientInstance instance, FleetMetricsViewModel vm, int characterId, string system)
    {
        var bus = instance.Services.GetRequiredService<IEventBus>();
        await bus.PublishAsync(new FleetMetricEvent(
            new MetricSample(characterId, FleetId, MetricKind.Location, 0, 0, system)));
        for (var i = 0; i < 100 && !vm.Members.Any(m => m.Location == system); i++)
            await Task.Delay(20);
    }

    // The rendered location readouts (a member sharing no location renders none, so these are only the coloured ones).
    private static IReadOnlyList<TextBlock> LocationBlocks(Control root, FleetMetricsViewModel vm) =>
        MemberHost(root, vm).GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("location") && t.IsVisible)
            .ToList();

    // The pop-out's own location readout, opened the way the row's button opens it — a separate window, so this is
    // the check that the colour rule reaches beyond the fleet-metrics screen.
    private static TextBlock OverlayLocation(DpsViewModel tracker)
    {
        var overlay = new DpsOverlayWindow(tracker) { Width = 320, Height = 200 };
        overlay.Show();
        Dispatcher.UIThread.RunJobs();
        overlay.UpdateLayout();

        return overlay.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Classes.Contains("location"));
    }

    // A real drag through the headless input pipeline: press on one row, cross the threshold, travel to another and
    // release — the same events the window's handlers see from a mouse.
    private static void DragRow(Window root, FleetMetricsViewModel vm, int from, int to)
    {
        ItemsControl host = MemberHost(root, vm);
        Point start = CentreOf(host, root, from);
        Point finish = CentreOf(host, root, to);

        root.MouseDown(start, MouseButton.Left);
        root.MouseMove(new Point(start.X + 6, start.Y));   // past the drag threshold, still over the same row
        root.MouseMove(finish);
        root.MouseUp(finish, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point CentreOf(ItemsControl host, Visual root, int index)
    {
        Control container = Assert.IsAssignableFrom<Control>(host.ContainerFromIndex(index));
        return container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), root)
            ?? throw new InvalidOperationException($"member row {index} is not in the tree");
    }

    private static async Task SeedOrderAsync(TestClientInstance instance, params int[] characterIds)
    {
        using var scope = instance.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new SetSettingCommand(
            FleetMetricsViewModel.OrderSettingKeyPrefix + FleetId,
            string.Join(",", characterIds)));
    }

    private static async Task<string?> ReadSettingAsync(TestClientInstance instance, string key)
    {
        using var scope = instance.Services.CreateScope();
        IReadOnlyList<SettingDto> settings = await scope.ServiceProvider
            .GetRequiredService<ICqrsDispatcher>().Query(new GetSettingsQuery());
        return settings.FirstOrDefault(s => s.Key == key)?.Value;
    }

    // The stored order is read asynchronously, so a freshly built view-model shows the roster order for a moment.
    private static async Task WaitForOrderAsync(FleetMetricsViewModel vm, params string[] names)
    {
        for (var i = 0; i < 100 && !vm.Members.Select(m => m.Character).SequenceEqual(names); i++)
            await Task.Delay(20);
    }

    // The badge's green, read from the live theme rather than pinned to a hex here: one place decides the colour.
    private static Color GreenBrush() =>
        Assert.IsAssignableFrom<ISolidColorBrush>(
            Application.Current?.FindResource("GreenBrush") ?? throw new InvalidOperationException("no GreenBrush")).Color;

    private static async Task PublishAsync(
        IEventBus bus, FleetMetricsViewModel vm, MetricKind kind, string? text, double value = 0)
    {
        foreach (var characterId in new[] { Commander, Member })
            await bus.PublishAsync(new FleetMetricEvent(new MetricSample(characterId, FleetId, kind, value, 0, text)));

        // Samples are routed onto the UI thread, so a published sample is not yet a rendered one.
        for (var i = 0; i < 100 && vm.Members.Any(m => kind is MetricKind.Location ? m.Location is null : m.Bounty == 0); i++)
            await Task.Delay(20);
    }

    // The one ItemsControl that hosts the member rows — found by its source, so the shell's own template parts
    // cannot be mistaken for it.
    private static ItemsControl MemberHost(Control root, FleetMetricsViewModel vm) =>
        root.GetVisualDescendants().OfType<ItemsControl>().First(c => ReferenceEquals(c.ItemsSource, vm.Members));

    private static IReadOnlyList<string> MemberTexts(Control root, FleetMetricsViewModel vm) =>
        MemberHost(root, vm).GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty)
            .Where(t => t.Length > 0)
            .ToList();

    // The rendered height of one member's row, margin included — the ItemsControl itself always fills the viewport.
    private static double RowHeight(Control root, FleetMetricsViewModel vm)
    {
        Control container = MemberHost(root, vm).ContainerFromIndex(0)
            ?? throw new InvalidOperationException("the member list rendered no rows");
        return container.Bounds.Height;
    }

    /// <summary>
    /// Every member row must be drawn by this screen's own item template. With no template the row falls through to
    /// the app-wide <see cref="ViewLocator"/>, which looks for a view named after the view-model, finds no
    /// <c>EveUtils.Client.Views.DpsView</c> (there is none — these rows have always been templated here) and renders
    /// its "Not Found: …" placeholder instead of a single metric.
    /// </summary>
    private static void AssertRowsAreTemplated(Control root, FleetMetricsViewModel vm, FleetMetricsLayout layout)
    {
        ItemsControl host = MemberHost(root, vm);

        Assert.DoesNotContain(MemberTexts(root, vm), t => t.StartsWith("Not Found", StringComparison.Ordinal));
        Assert.Null(Assert.IsAssignableFrom<Control>(host.ContainerFromIndex(0))
            .GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text?.Contains("DpsView", StringComparison.Ordinal) == true)?.Text);

        // The template markers themselves: a graph where the layout promises one, the mono figures where it does not.
        Assert.Equal(layout is FleetMetricsLayout.Compact ? 0 : vm.Members.Count,
            host.GetVisualDescendants().OfType<DpsGraph>().Count());
        Assert.True(HasFigure(MemberTexts(root, vm), "OUT"), "no member row rendered its DPS-out figure");
    }

    private static bool HasFigure(IReadOnlyList<string> texts, string label) =>
        texts.Any(t => t.StartsWith(label, StringComparison.Ordinal));

    private static async Task<string?> ReadStoredLayoutAsync(TestClientInstance instance)
    {
        using var scope = instance.Services.CreateScope();
        IReadOnlyList<SettingDto> settings = await scope.ServiceProvider
            .GetRequiredService<ICqrsDispatcher>().Query(new GetSettingsQuery());
        return settings.FirstOrDefault(s => s.Key == FleetMetricsViewModel.LayoutSettingKey)?.Value;
    }

    [AvaloniaFact]
    public async Task Layout_DefaultsToList_WhenNothingIsStored()
    {
        using var instance = CreateInstance();
        var vm = await BuildViewModelAsync(instance, Roster());

        Assert.Equal(FleetMetricsLayout.List, vm.Layout);
        Assert.True(vm.IsListLayout);
        Assert.True(vm.ShowsGraphs);
        Assert.Null(await ReadStoredLayoutAsync(instance));
    }

    [AvaloniaFact]
    public async Task Layout_IsRestored_ByTheNextWindowOnTheSameInstall()
    {
        using var instance = CreateInstance();
        var first = await BuildViewModelAsync(instance, Roster());

        first.SetLayoutCommand.Execute(FleetMetricsLayout.Compact);
        for (var i = 0; i < 100 && await ReadStoredLayoutAsync(instance) is null; i++)
            await Task.Delay(20);
        Assert.Equal("compact", await ReadStoredLayoutAsync(instance));
        first.Dispose();

        // A second window is what the next session sees: a fresh view-model over the same settings store.
        var second = await BuildViewModelAsync(instance, Roster());
        for (var i = 0; i < 100 && second.Layout is FleetMetricsLayout.List; i++)
            await Task.Delay(20);
        Assert.Equal(FleetMetricsLayout.Compact, second.Layout);
        second.Dispose();
    }

    // Whether the screen stands in its own window or docked as a tab, its rows are its own — a row that falls
    // through to the ViewLocator shows "Not Found: EveUtils.Client.Views.DpsView" and not one metric.
    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.List, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Grid, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Grid, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Compact, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Compact, Shell.DockedTab)]
    public async Task MemberRows_AreDrawnByTheirOwnTemplate_InEveryLayoutAndShell(
        FleetMetricsLayout layout, Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, layout, shell);

        AssertRowsAreTemplated(root, vm, layout);
    }

    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task ListLayout_ShowsEveryFigure_AndAGraphPerMember(Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, FleetMetricsLayout.List, shell);

        var texts = MemberTexts(root, vm);
        Assert.Contains("Lionear", texts);
        Assert.True(HasFigure(texts, "OUT"));
        Assert.True(HasFigure(texts, "IN"));
        Assert.True(HasFigure(texts, "CAP"));
        Assert.True(HasFigure(texts, "NEUT"));
        Assert.Contains(texts, t => t.StartsWith("◉ Jita", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("ISK", StringComparison.Ordinal));
        AssertRowsAreTemplated(root, vm, FleetMetricsLayout.List);
    }

    // Grid gives up the bounty only: every live combat figure survives, one size down, and the graph comes along.
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task GridLayout_DropsOnlyTheBounty_AndKeepsEveryLiveFigureBesideTheGraph(Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, FleetMetricsLayout.Grid, shell);

        var texts = MemberTexts(root, vm);
        Assert.Contains("Lionear", texts);
        Assert.True(HasFigure(texts, "OUT"));
        Assert.True(HasFigure(texts, "IN"));
        Assert.True(HasFigure(texts, "CAP"));
        Assert.True(HasFigure(texts, "NEUT"));
        Assert.Contains(texts, t => t.StartsWith("◉ Jita", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("ISK", StringComparison.Ordinal));

        AssertRowsAreTemplated(root, vm, FleetMetricsLayout.Grid);
        Assert.Single(MemberHost(root, vm).GetVisualDescendants().OfType<WrapPanel>());
    }

    // A squeezed graph reads as nothing at all, so the grid card owes its graph real vertical range — the reason the
    // figures sit above it rather than in a column beside it.
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task GridLayout_GivesTheGraphAReadableShape(Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, FleetMetricsLayout.Grid, shell);

        DpsGraph graph = MemberHost(root, vm).GetVisualDescendants().OfType<DpsGraph>().First();

        Assert.True(graph.Bounds.Height >= 70,
            $"a grid graph needs vertical range to be read at all, got {graph.Bounds.Height}");
        Assert.True(graph.Bounds.Width / graph.Bounds.Height < 6,
            $"a grid graph flatter than 6:1 is a band, not a graph (got {graph.Bounds.Width}x{graph.Bounds.Height})");
    }

    // Compact gives up the graph — that is what buys the density — and the bounty. Every live figure stays: a full
    // row has the width for all four.
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task CompactLayout_DropsTheGraphAndTheBounty_ButKeepsEveryLiveFigure(Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, FleetMetricsLayout.Compact, shell);

        var texts = MemberTexts(root, vm);
        Assert.Contains("Lionear", texts);
        Assert.Contains("RaymondKrah", texts);
        Assert.True(HasFigure(texts, "OUT"));
        Assert.True(HasFigure(texts, "IN"));
        Assert.True(HasFigure(texts, "CAP"));
        Assert.True(HasFigure(texts, "NEUT"));
        Assert.Contains(texts, t => t.StartsWith("◉ Jita", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("ISK", StringComparison.Ordinal));
        AssertRowsAreTemplated(root, vm, FleetMetricsLayout.Compact);

        // No graphs left to explain, so the line legend goes with them.
        Assert.False(vm.ShowsGraphs);
    }

    // Density is the whole point of the compact layout, so measure the rendered row rather than trust the template.
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task CompactLayout_FitsFarMoreMembersOnScreen_ThanTheListLayout(Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, FleetMetricsLayout.List, shell);
        double listRow = RowHeight(root, vm);

        vm.SetLayoutCommand.Execute(FleetMetricsLayout.Compact);
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        double compactRow = RowHeight(root, vm);

        Assert.True(compactRow < listRow / 3,
            $"a compact row should take under a third of a list row (list {listRow}, compact {compactRow})");
    }

    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.List, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Grid, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Grid, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Compact, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Compact, Shell.DockedTab)]
    public async Task CommanderBadge_StaysInTheHeader_InEveryLayoutAndShell(FleetMetricsLayout layout, Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, layout, shell);

        var badge = Assert.Single(root.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("chip"));
        var text = Assert.IsType<TextBlock>(badge.Child);
        Assert.Equal("◉ 2/2 WITH FC", text.Text);
        Assert.True(vm.CommanderPresence.IsComplete);
    }

    // ET-43: standing with the FC is a colour, not a name you have to read and compare. The commander counts as
    // standing with themselves, exactly as the header badge counts them.
    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.List, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Grid, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Grid, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Compact, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Compact, Shell.DockedTab)]
    public async Task Location_TurnsGreen_ForMembersStandingWithTheCommander(FleetMetricsLayout layout, Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, layout, shell);

        // Both members share Jita, the commander's own system, so both readouts carry the badge's green.
        Assert.All(LocationBlocks(root, vm), block => Assert.Contains("withfc", block.Classes));
        Assert.Equal(GreenBrush(), Assert.IsAssignableFrom<ISolidColorBrush>(
            LocationBlocks(root, vm).First().Foreground).Color);
    }

    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Grid, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Compact, Shell.DockedTab)]
    public async Task Location_StaysNeutral_ForAMemberAwayFromTheCommander(FleetMetricsLayout layout, Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, layout, shell);

        await MoveAsync(instance, vm, Member, "Perimeter");
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();

        var blocks = LocationBlocks(root, vm);
        Assert.Contains(blocks, b => b.Classes.Contains("withfc"));       // the commander, in their own system
        Assert.Contains(blocks, b => !b.Classes.Contains("withfc"));      // the straggler in Perimeter
        Assert.NotEqual(GreenBrush(), Assert.IsAssignableFrom<ISolidColorBrush>(
            blocks.First(b => !b.Classes.Contains("withfc")).Foreground).Color);
    }

    // No commander system to compare against is not a reason to mark anybody present — the badge reads unknown and
    // every location stays neutral rather than showing half a signal.
    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Compact, Shell.DockedTab)]
    public async Task Location_StaysNeutralEverywhere_WhenTheCommanderSharesNoLocation(
        FleetMetricsLayout layout, Shell shell)
    {
        using var instance = CreateInstance();
        var vm = await BuildViewModelAsync(instance, Roster());
        var bus = instance.Services.GetRequiredService<IEventBus>();

        // Location sharing is opt-in: the member reports, the commander does not.
        await bus.PublishAsync(new FleetMetricEvent(
            new MetricSample(Member, FleetId, MetricKind.Location, 0, 0, "Jita")));
        for (var i = 0; i < 100 && !vm.Members.Any(m => m.Location == "Jita"); i++)
            await Task.Delay(20);

        vm.SetLayoutCommand.Execute(layout);
        Control root = await ShowExistingAsync(vm, shell);

        Assert.True(vm.CommanderPresence.IsUnknown);

        // Exactly the one member who shares a location renders one — asserting "none are green" over an empty set
        // would pass for the wrong reason.
        TextBlock block = Assert.Single(LocationBlocks(root, vm));
        Assert.Equal("◉ Jita", block.Text);
        Assert.DoesNotContain("withfc", block.Classes);
    }

    // The pop-out is its own window, so it inherits nothing from the screen that opened it — but it shows the same
    // tracker, and must therefore read the same. The colour rule is application-level for exactly this reason.
    [AvaloniaFact]
    public async Task PopOut_ShowsTheSameLocationColour_AsTheRowItWasOpenedFrom()
    {
        using var instance = CreateInstance();
        var (_, vm) = await ShowAsync(instance, FleetMetricsLayout.List, Shell.OwnWindow);

        await MoveAsync(instance, vm, Member, "Perimeter");
        Dispatcher.UIThread.RunJobs();

        TextBlock withCommander = OverlayLocation(vm.Members.First(m => m.Character == "RaymondKrah"));
        TextBlock away = OverlayLocation(vm.Members.First(m => m.Character == "Lionear"));

        Assert.Contains("withfc", withCommander.Classes);
        Assert.DoesNotContain("withfc", away.Classes);
        Assert.Equal(GreenBrush(), Assert.IsAssignableFrom<ISolidColorBrush>(withCommander.Foreground).Color);
        Assert.NotEqual(GreenBrush(), Assert.IsAssignableFrom<ISolidColorBrush>(away.Foreground).Color);
    }

    // An open pop-out is a live readout, not a snapshot: when the member or the FC jumps, its colour moves too.
    [AvaloniaFact]
    public async Task PopOut_FollowsTheCommander_WhileItStaysOpen()
    {
        using var instance = CreateInstance();
        var (_, vm) = await ShowAsync(instance, FleetMetricsLayout.List, Shell.OwnWindow);

        DpsViewModel member = vm.Members.First(m => m.Character == "Lionear");
        TextBlock location = OverlayLocation(member);
        Assert.Contains("withfc", location.Classes);

        // The member leaves the commander's system…
        await MoveAsync(instance, vm, Member, "Perimeter");
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("withfc", location.Classes);

        // …and then the commander follows them there.
        await MoveAsync(instance, vm, Commander, "Perimeter");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("withfc", location.Classes);
        Assert.Equal(GreenBrush(), Assert.IsAssignableFrom<ISolidColorBrush>(location.Foreground).Color);
    }

    [AvaloniaFact]
    public async Task PopOut_StaysNeutral_WhenTheCommanderSharesNoLocation()
    {
        using var instance = CreateInstance();
        var vm = await BuildViewModelAsync(instance, Roster());

        // Location sharing is opt-in: the member reports, the commander does not.
        await MoveAsync(instance, vm, Member, "Jita");
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.CommanderPresence.IsUnknown);
        TextBlock location = OverlayLocation(vm.Members.First(m => m.Character == "Lionear"));
        Assert.Equal("◉ Jita", location.Text);
        Assert.DoesNotContain("withfc", location.Classes);
        vm.Dispose();
    }

    // The third way this screen is presented: a module opened docked and then floated (or back) hands its content
    // between a tab and its own window. The rows have to survive the migration, templates and colours intact.
    [AvaloniaFact]
    public async Task MemberRows_SurviveADockToFloatMigration()
    {
        using var instance = CreateInstance();
        var vm = await BuildViewModelAsync(instance, Roster());
        var bus = instance.Services.GetRequiredService<IEventBus>();
        await PublishAsync(bus, vm, MetricKind.Location, "Jita");

        var display = new FakeDisplay { IsFloating = false };
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        var window = new FleetMetricsWindow(vm) { Width = 900, Height = 620 };
        host.Open(window, "FLEET METRICS", "fleet");

        // Docked → floating: the service hands the content back to the module's own window and shows it.
        display.IsFloating = true;
        host.SwitchMode();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        AssertRowsAreTemplated(window, vm, FleetMetricsLayout.List);
        Assert.All(LocationBlocks(window, vm), block => Assert.Contains("withfc", block.Classes));
        window.Close();
    }

    // ET-28: dragging is wired once on the shared ItemsControl, so it has to work identically whichever template
    // drew the rows — and in the docked tab, where the handlers travel with the reparented content.
    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.List, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Grid, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Grid, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Compact, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Compact, Shell.DockedTab)]
    public async Task Drag_ReordersMembers_InEveryLayoutAndShell(FleetMetricsLayout layout, Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, layout, shell);
        Assert.Equal(["RaymondKrah", "Lionear"], vm.Members.Select(m => m.Character));

        DragRow(root, vm, from: 1, to: 0);

        Assert.Equal(["Lionear", "RaymondKrah"], vm.Members.Select(m => m.Character));
    }

    [AvaloniaFact]
    public async Task Drag_PersistsTheOrder_ForTheNextViewModelOnTheSameFleet()
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, FleetMetricsLayout.List, Shell.OwnWindow);

        DragRow(root, vm, from: 1, to: 0);
        for (var i = 0; i < 100 && await ReadSettingAsync(instance, vm.OrderSettingKey) is null; i++)
            await Task.Delay(20);
        Assert.Equal($"{Member},{Commander}", await ReadSettingAsync(instance, vm.OrderSettingKey));
        vm.Dispose();

        // A second view-model is what the next session — or a roster refresh — sees.
        var next = await BuildViewModelAsync(instance, Roster());
        await WaitForOrderAsync(next, "Lionear", "RaymondKrah");
        Assert.Equal(["Lionear", "RaymondKrah"], next.Members.Select(m => m.Character));
        next.Dispose();
    }

    // A character the stored order has never seen joins at the back, which is where an unarranged fleet grows.
    [AvaloniaFact]
    public async Task Order_PutsACharacterItDoesNotKnow_AtTheBack()
    {
        using var instance = CreateInstance();
        await SeedOrderAsync(instance, Member, Commander);

        var vm = await BuildViewModelAsync(instance, Roster());
        await WaitForOrderAsync(vm, "Lionear", "RaymondKrah");

        // A straggler who is not on the roster turns up through a sample.
        await MoveAsync(instance, vm, Latecomer, "Jita");

        Assert.Equal(["Lionear", "RaymondKrah", "Tarek"], vm.Members.Select(m => m.Character));
        vm.Dispose();
    }

    // A stored id whose character has left the fleet matches no row, so it costs nothing: no gap, no error, and the
    // members that are still here keep the sequence the order gives them.
    [AvaloniaFact]
    public async Task Order_IgnoresAStoredIdThatIsNoLongerInTheFleet()
    {
        using var instance = CreateInstance();
        await SeedOrderAsync(instance, Stranger, Member, Stranger + 1, Commander);

        var vm = await BuildViewModelAsync(instance, Roster());
        await WaitForOrderAsync(vm, "Lionear", "RaymondKrah");

        Assert.Equal(["Lionear", "RaymondKrah"], vm.Members.Select(m => m.Character));
        Assert.DoesNotContain(vm.Members, m => m.CharacterId == Stranger);
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Order_DropsTheTailRatherThanHalfAnId_WhenItOutgrowsTheSettingValue()
    {
        int[] order = Enumerable.Range(90_000_000, 120).ToArray();

        string value = FleetMetricsViewModel.JoinOrder(order);

        Assert.True(value.Length <= 512, $"a setting value holds 512 characters, got {value.Length}");
        Assert.Equal(order.Take(FleetMetricsViewModel.ParseOrder(value).Count), FleetMetricsViewModel.ParseOrder(value));
    }

    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.Grid, "The bounty figure shows in the list view")]
    [InlineData(FleetMetricsLayout.Compact, "Graphs and the bounty figure show in the list view")]
    public async Task LayoutHint_NamesWhatTheDensityDrops(FleetMetricsLayout layout, string dropped)
    {
        using var instance = CreateInstance();
        var vm = await BuildViewModelAsync(instance, Roster());

        vm.SetLayoutCommand.Execute(layout);

        Assert.Contains(dropped, vm.LayoutHint, StringComparison.Ordinal);
        vm.Dispose();
    }
}
