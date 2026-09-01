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
using EveUtils.Client.Notifications;
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

    private static TestClientInstance CreateInstance(RecordingToastService? toasts = null) => TestClientInstance.Create(services =>
    {
        services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
        {
            [Commander] = "RaymondKrah",
            [Member] = "Lionear",
            [Latecomer] = "Tarek",
        });
        if (toasts is not null)
            services.AddSingleton<IToastService>(toasts);
    });

    private static FakeFleetClient Roster() => new()
    {
        Members =
        [
            new FleetMemberInfo(1, Commander, -1, -1, FleetRole.FleetCommander, false),
            new FleetMemberInfo(2, Member, 1, 1, FleetRole.SquadMember, false),
        ],
    };

    // Enough members to fill the widest row the grid ever draws (four columns at 1400), so "does the last column end
    // flush with the panel" is a question the fixture can actually answer. A half-empty last row is the panel doing
    // its job — it keeps the empty columns — not a strip of whitespace.
    private static FakeFleetClient CrowdedRoster() => RosterOf(8);

    private static FakeFleetClient RosterOf(int members) => new()
    {
        Members = [
            new FleetMemberInfo(1, Commander, -1, -1, FleetRole.FleetCommander, false),
            .. Enumerable.Range(0, members - 1).Select(i =>
                new FleetMemberInfo(i + 2, Member + i, 1, 1, FleetRole.SquadMember, false)),
        ],
    };

    // Every render assertion needs the roster pre-fill to have landed, otherwise it asserts against an empty list.
    private static async Task<FleetMetricsViewModel> BuildViewModelAsync(
        TestClientInstance instance, IFleetClient fleets, int expectedMembers = 2)
    {
        var vm = new FleetMetricsViewModel(instance.Services, fleets, Op);
        for (var i = 0; i < 100 && vm.Members.Count < expectedMembers; i++)
            await Task.Delay(20);
        Assert.Equal(expectedMembers, vm.Members.Count);
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
    private static Task<(Window Root, FleetMetricsViewModel Vm)> ShowAsync(
        TestClientInstance instance, FleetMetricsLayout layout, Shell shell) =>
        ShowAsync(instance, layout, shell, 900);

    private static async Task<(Window Root, FleetMetricsViewModel Vm)> ShowAsync(
        TestClientInstance instance, FleetMetricsLayout layout, Shell shell, double width,
        FakeFleetClient? fleets = null)
    {
        fleets ??= Roster();
        var vm = await BuildViewModelAsync(instance, fleets, fleets.Members.Count);
        var bus = instance.Services.GetRequiredService<IEventBus>();
        await PublishAsync(bus, vm, MetricKind.Location, "Jita");
        await PublishAsync(bus, vm, MetricKind.Bounty, null, 5_000_000);

        vm.SetLayoutCommand.Execute(layout);

        var window = new FleetMetricsWindow(vm) { Width = width, Height = 620 };
        Window root = window;
        if (shell is Shell.DockedTab)
        {
            var display = new FakeDisplay { IsFloating = false };
            var host = new ModuleHostService();
            host.SetOwner(new Window());
            host.SetHost(display);
            host.Open(window, "FLEET METRICS", "fleet", "fleet-metrics");

            // Stand the reparented content in a plain window: the module's own window is deliberately not the host,
            // which is exactly what this path has to survive.
            root = new Window { Width = width, Height = 620, Content = Assert.Single(display.HostTabs).Content };
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
            host.Open(window, "FLEET METRICS", "fleet", "fleet-metrics");
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
    // release — the same events the window's handlers see from a mouse. Aims just past the target row's leading
    // edge, which is where the member is asked to land in front of it.
    private static void DragRow(Window root, FleetMetricsViewModel vm, int from, int to)
    {
        HoldRow(root, vm, from, to);
        root.MouseUp(LeadingEdgeOf(MemberHost(root, vm), root, to), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // The same drag, stopped mid-gesture and still held, so a test can look at what the screen is showing.
    private static void HoldRow(Window root, FleetMetricsViewModel vm, int from, int to)
    {
        ItemsControl host = MemberHost(root, vm);
        Point start = CentreOf(host, root, from);

        root.MouseDown(start, MouseButton.Left);
        root.MouseMove(new Point(start.X + 6, start.Y));   // past the drag threshold, still over the same row
        root.MouseMove(LeadingEdgeOf(host, root, to));
        Dispatcher.UIThread.RunJobs();
    }

    // A point just inside a row's leading edge — above its middle in a stacked layout, left of it when the cards
    // stand beside each other — so the drop lands in front of that row rather than behind it. Read off where the
    // containers landed, the same rule the window's own drop marker follows.
    private static Point LeadingEdgeOf(ItemsControl host, Visual root, int index)
    {
        Control container = Assert.IsAssignableFrom<Control>(host.ContainerFromIndex(index));
        Point centre = CentreOf(host, root, index);
        return SideBySide(host)
            ? new Point(centre.X - container.Bounds.Width / 4, centre.Y)
            : new Point(centre.X, centre.Y - container.Bounds.Height / 4);
    }

    // Whether the first two containers share a row rather than stack.
    private static bool SideBySide(ItemsControl host) =>
        host.ItemsPanelRoot is { Children.Count: >= 2 } panel
        && panel.Children[1].Bounds.X > panel.Children[0].Bounds.X
        && panel.Children[1].Bounds.Y < panel.Children[0].Bounds.Bottom;

    private static Point CentreOf(ItemsControl host, Visual root, int index)
    {
        Control container = Assert.IsAssignableFrom<Control>(host.ContainerFromIndex(index));
        return container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), root)
            ?? throw new InvalidOperationException($"member row {index} is not in the tree");
    }

    // The drag layer's parts, found by name so a test reads them the way the window builds them.
    private static Border Ghost(Control root) =>
        root.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DragGhost");

    private static ContentControl GhostContent(Control root) =>
        root.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Name == "DragGhostContent");

    private static Border Marker(Control root) =>
        root.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DropMarker");

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
        Assert.Single(MemberHost(root, vm).GetVisualDescendants().OfType<FillGridPanel>());
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

    /// <summary>
    /// ET-108. A card is at its widest one pixel before a second column fits — just under twice the minimum, 643px
    /// here — while its height stays 176. That is the "flattened band" the card's own comment warns about, so it was
    /// rendered and looked at before the height was left alone: at that stand the graph is 621x100 (6.2:1) and all
    /// four lines are still cleanly separated, because <see cref="DpsGraph"/> plots at a FIXED time density. A wider
    /// graph shows a longer timeline at the same vertical range; it does not stretch the same curve flat. The 100px
    /// of vertical range — the thing that makes four lines readable — is identical at every width.
    ///
    /// So the invariant that matters on a card that can double in width is the graph's HEIGHT, not its aspect ratio.
    /// Growing the height along with the width (318:176) would put the widest card at 356px tall — one card per row
    /// on a 620-high window, which is the density the grid layout exists for, spent on nothing.
    /// </summary>
    // The two hosts reserve different chrome around the card panel — the window keeps 30px for its margin and
    // scrollbar, the docked tab 28 — so the last single-column width differs by two between them. Both land the
    // panel itself on 643, one pixel short of a second 318.
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow, 673)]
    [InlineData(Shell.DockedTab, 671)]
    public async Task GridLayout_KeepsTheGraphsVerticalRange_EvenOnTheWidestCard(Shell shell, int width)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, FleetMetricsLayout.Grid, shell, width, CrowdedRoster());

        ItemsControl host = MemberHost(root, vm);
        Assert.False(SideBySide(host), $"{width} should still be a single column of cards");

        Rect card = Assert.IsAssignableFrom<Control>(host.ContainerFromIndex(0)).Bounds;
        Assert.True(card.Width > 600, $"this is meant to be the widest a card gets, got {card.Width}");

        DpsGraph graph = host.GetVisualDescendants().OfType<DpsGraph>().First();
        Assert.True(graph.Bounds.Height >= 70,
            $"the widest card lost the graph's vertical range, got {graph.Bounds.Height}");
    }

    // ET-108. The card width is a minimum now, not a size: whatever the window is, the cards divide the row between
    // them and no strip of whitespace is left on the right. 520 is the window's own MinWidth, 720 its default.
    [AvaloniaTheory]
    [InlineData(520, Shell.OwnWindow)]
    [InlineData(720, Shell.OwnWindow)]
    [InlineData(1000, Shell.OwnWindow)]
    [InlineData(1400, Shell.OwnWindow)]
    [InlineData(520, Shell.DockedTab)]
    [InlineData(720, Shell.DockedTab)]
    [InlineData(1000, Shell.DockedTab)]
    [InlineData(1400, Shell.DockedTab)]
    public async Task GridLayout_FillsTheWidth_LeavingNoStripOnTheRight(int width, Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, FleetMetricsLayout.Grid, shell, width, CrowdedRoster());

        AssertGridFillsItsWidth(root, vm);
    }

    // The third presentation path. A card grid measured while docked and then floated must divide the NEW width, not
    // keep the columns it was arranged with in the tab.
    [AvaloniaFact]
    public async Task GridLayout_FillsTheWidth_AfterADockToFloatMigration()
    {
        using var instance = CreateInstance();
        FakeFleetClient fleets = CrowdedRoster();
        var vm = await BuildViewModelAsync(instance, fleets, fleets.Members.Count);
        vm.SetLayoutCommand.Execute(FleetMetricsLayout.Grid);

        var display = new FakeDisplay { IsFloating = false };
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        var window = new FleetMetricsWindow(vm) { Width = 1000, Height = 620 };
        host.Open(window, "FLEET METRICS", "fleet", "fleet-metrics");

        var docked = new Window { Width = 560, Height = 620, Content = Assert.Single(display.HostTabs).Content };
        docked.Show();
        Dispatcher.UIThread.RunJobs();
        docked.UpdateLayout();
        AssertGridFillsItsWidth(docked, vm);

        // Hand the content back before floating it: this stand-in host is only here so the docked tab has somewhere
        // to lay out, and SwitchMode reparents the very same DockPanel into the module's own window.
        docked.Content = null;
        docked.Close();
        Dispatcher.UIThread.RunJobs();

        display.IsFloating = true;
        host.SwitchMode();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        AssertGridFillsItsWidth(window, vm);
        window.Close();
    }

    // The cards divide the panel: the last column ends flush with it, and none is narrower than the minimum unless
    // the panel itself is (the narrow docked tab, where one column takes the whole width).
    private static void AssertGridFillsItsWidth(Control root, FleetMetricsViewModel vm)
    {
        ItemsControl host = MemberHost(root, vm);
        FillGridPanel panel = Assert.Single(host.GetVisualDescendants().OfType<FillGridPanel>());
        Rect[] cards = panel.Children.Select(c => c.Bounds).ToArray();

        Assert.NotEmpty(cards);
        Assert.True(panel.Bounds.Width > 0, "the card panel rendered with no width");
        Assert.Equal(panel.Bounds.Width, cards.Max(c => c.Right), 1);
        Assert.All(cards, c => Assert.True(
            c.Width >= Math.Min(panel.Bounds.Width, 318) - 1,
            $"a card fell below the minimum in a panel of {panel.Bounds.Width}: {c.Width}"));
    }

    /// <summary>
    /// The cards start at the TOP of the list, not floating in the middle of it. A panel whose
    /// <c>ArrangeOverride</c> hands back less than the rect it was given is centred in the remainder — Avalonia's
    /// <c>ArrangeCore</c> puts <c>VerticalAlignment.Stretch</c> on the same branch as <c>Center</c> — which parked a
    /// five-card grid halfway down its viewport, with a gap between the legend and the first row and a smaller one
    /// underneath. The <see cref="WrapPanel"/> this replaced returned its full arrange size and never tripped it.
    /// Measured at the panel, not at the ItemsControl: everything above it stayed at Y=0 the whole time.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task GridLayout_StartsAtTheTop_WhenTheCardsDoNotFillTheViewport(Shell shell)
    {
        using var instance = CreateInstance();

        // Five members over three columns at 1000: two rows of cards, shorter than the viewport. The operator's case.
        var (root, vm) = await ShowAsync(instance, FleetMetricsLayout.Grid, shell, 1000, RosterOf(5));

        ItemsControl host = MemberHost(root, vm);
        FillGridPanel panel = Assert.Single(host.GetVisualDescendants().OfType<FillGridPanel>());
        Assert.True(panel.DesiredSize.Height < panel.Bounds.Height,
            "this only bites when the cards are shorter than the viewport, and here they are not");

        Assert.Equal(0, panel.Bounds.Y, 1);
        Assert.Equal(0, (panel.TranslatePoint(default, host) ?? new Point(0, -1)).Y, 1);

        Control first = Assert.IsAssignableFrom<Control>(host.ContainerFromIndex(0));
        Assert.Equal(0, (first.TranslatePoint(default, host) ?? new Point(0, -1)).Y, 1);
    }

    /// <summary>The other side of that rule: filling the viewport must not cost the rows below the fold. A grid
    /// taller than its viewport still reports the taller extent and still scrolls to the cards underneath.</summary>
    [AvaloniaFact]
    public async Task GridLayout_StillScrolls_WhenTheCardsOutgrowTheViewport()
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, FleetMetricsLayout.Grid, Shell.OwnWindow, 1000, RosterOf(12));

        ItemsControl host = MemberHost(root, vm);
        ScrollViewer scroller = root.GetVisualDescendants().OfType<ScrollViewer>()
            .First(s => s.GetVisualDescendants().Contains(host));

        Assert.True(scroller.Extent.Height > scroller.Viewport.Height,
            $"twelve cards should outgrow the viewport, got extent {scroller.Extent.Height} in {scroller.Viewport.Height}");

        Control first = Assert.IsAssignableFrom<Control>(host.ContainerFromIndex(0));
        double before = (first.TranslatePoint(default, root) ?? default).Y;

        scroller.Offset = scroller.Offset.WithY(120);
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();

        Assert.Equal(120, scroller.Offset.Y, 1);
        Assert.Equal(before - 120, (first.TranslatePoint(default, root) ?? default).Y, 1);
    }

    // ET-108's real trap: the drop marker used to read the PANEL TYPE to decide whether it stands between two
    // columns or between two rows, which breaks silently the moment the panel is swapped. It reads the containers'
    // own positions now — so a grid wide enough for two columns marks vertically...
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task Drag_MarksBetweenColumns_WhenTheGridCardsStandSideBySide(Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, FleetMetricsLayout.Grid, shell, 900);
        Assert.True(SideBySide(MemberHost(root, vm)), "900 should still give the grid two columns");

        HoldRow(root, vm, from: 1, to: 0);

        Border marker = Marker(root);
        Assert.True(marker.IsVisible, "nothing shows where the member would land");
        Assert.True(marker.Height > marker.Width,
            $"the drop marker lies flat between two side-by-side cards ({marker.Width}x{marker.Height})");
    }

    // ...and the same grid squeezed to a single column marks horizontally, like the stacked layouts it now resembles.
    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.Grid)]
    [InlineData(FleetMetricsLayout.List)]
    public async Task Drag_MarksBetweenRows_WhenTheCardsAreStacked(FleetMetricsLayout layout)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, layout, Shell.DockedTab, 420);
        Assert.False(SideBySide(MemberHost(root, vm)), "420 is too narrow for two columns of anything");

        HoldRow(root, vm, from: 1, to: 0);

        Border marker = Marker(root);
        Assert.True(marker.IsVisible, "nothing shows where the member would land");
        Assert.True(marker.Width > marker.Height,
            $"the drop marker stands upright between two stacked rows ({marker.Width}x{marker.Height})");
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
            host.Open(window, "FLEET METRICS", "fleet", "fleet-metrics");

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

    // While a member is held the list must stand still and say what is happening: a ghost of the row under the
    // cursor, the row it came from faded in place, and a marker on the spot it would land.
    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.List, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Grid, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Grid, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Compact, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Compact, Shell.DockedTab)]
    public async Task Drag_ShowsAGhostAndAMarker_AndLeavesTheListStill(FleetMetricsLayout layout, Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, layout, shell);
        DpsViewModel held = vm.Members[1];

        HoldRow(root, vm, from: 1, to: 0);

        Border ghost = Ghost(root);
        Assert.True(ghost.IsVisible, "nothing is following the cursor to show what is being held");
        Assert.Same(held, GhostContent(root).Content);
        Assert.True(ghost.Bounds.Width > 0 && ghost.Bounds.Height > 0, "the ghost rendered with no size");
        Assert.True(Marker(root).IsVisible, "nothing shows where the member would land");
        Assert.True(held.IsDragging, "the row it came from is not marked as the place it left");

        // Nothing has actually moved yet — that is the whole point of holding it.
        Assert.Equal(["RaymondKrah", "Lionear"], vm.Members.Select(m => m.Character));

        root.MouseUp(new Point(10, 10), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task Drag_LeavesNothingBehind_AndKeepsTheOldOrder_WhenEscapeCancelsIt(Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, FleetMetricsLayout.List, shell);
        DpsViewModel held = vm.Members[1];

        HoldRow(root, vm, from: 1, to: 0);
        root.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, keySymbol: null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["RaymondKrah", "Lionear"], vm.Members.Select(m => m.Character));
        Assert.False(Ghost(root).IsVisible, "the ghost outlived the drag");
        Assert.False(Marker(root).IsVisible, "the drop marker outlived the drag");
        Assert.False(held.IsDragging, "the row stayed faded after the drag was cancelled");
        Assert.Null(await ReadSettingAsync(instance, vm.OrderSettingKey));
    }

    // Dropping past the last member sends it to the end — the marker sits after the last row for exactly this.
    [AvaloniaFact]
    public async Task Drag_SendsAMemberToTheEnd_WhenItIsDroppedPastTheLastRow()
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, FleetMetricsLayout.List, Shell.OwnWindow);
        ItemsControl host = MemberHost(root, vm);
        Point start = CentreOf(host, root, 0);

        root.MouseDown(start, MouseButton.Left);
        root.MouseMove(new Point(start.X + 6, start.Y));
        root.MouseMove(new Point(start.X, CentreOf(host, root, 1).Y + 400));   // empty space below the list
        Dispatcher.UIThread.RunJobs();
        Assert.True(Marker(root).IsVisible);
        root.MouseUp(new Point(start.X, CentreOf(host, root, 1).Y + 400), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

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
        int[] order = Enumerable.Range(90_000_000, 500).ToArray();

        string value = FleetMetricsViewModel.JoinOrder(order);

        Assert.True(value.Length <= 4000, $"a setting value holds 4000 characters, got {value.Length}");
        Assert.Equal(order.Take(FleetMetricsViewModel.ParseOrder(value).Count), FleetMetricsViewModel.ParseOrder(value));
    }

    // The setting value is now sized for EVE's own 256-member fleet cap with room to spare, but a runaway order
    // (or a future setting change that shrinks the margin again) should still tell the FC rather than fail quietly.
    [AvaloniaFact]
    public async Task PersistOrderAsync_WarnsAndDropsTheTail_WhenTheOrderOutgrowsTheSettingValue()
    {
        var toasts = new RecordingToastService();
        using var instance = CreateInstance(toasts);
        var vm = await BuildViewModelAsync(instance, Roster());

        int[] order = Enumerable.Range(90_000_000, 500).ToArray();
        await vm.PersistOrderAsync(order);

        string? stored = await ReadSettingAsync(instance, vm.OrderSettingKey);
        int kept = FleetMetricsViewModel.ParseOrder(stored).Count;
        Assert.True(kept < order.Length, "the seeded order was meant to overflow the setting value");

        var toast = Assert.Single(toasts.Toasts);
        Assert.Equal("Order not fully saved", toast.Title);
        Assert.Equal(ToastKind.Warning, toast.Kind);
        Assert.Contains($"first {kept} of {order.Length}", toast.Message);

        vm.Dispose();
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
