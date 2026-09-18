using System.Collections.ObjectModel;
using Avalonia;
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
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-310: RunsFilterTilesTests drives <see cref="RunFilterTileViewModel"/> directly, so it never noticed the tile
/// had stopped answering a real click — <c>Button</c>'s own class handling marks a pointer release (and a keyboard
/// activation) <c>Handled</c> before a plain XAML-wired instance handler on that same element ever runs, so ET-293's
/// <c>PointerReleased</c>/<c>KeyDown</c> attributes were dead from the moment they shipped. These drive the same real
/// pointer/keyboard input the operator does (<c>MouseDown</c>/<c>MouseUp</c>, <c>KeyPress</c>) through the actual
/// <see cref="RunsWindow"/> tree, the same pattern <c>CharacterPickerToggleTests</c> uses for the multi-select picker.
/// </summary>
public sealed class RunsFilterTileInputTests
{
    private static readonly DateTime StartedAtUtc = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<Character> Crew =
    [
        new("Ra Vinter", 90000001), new("Kav Orn", 90000002)
    ];

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    private sealed record Presented(Window Root, Control Content, RunsOverviewViewModel ViewModel);

    private static async Task _SaveAsync(ICqrsDispatcher dispatcher, long characterId, ActivityKind kind,
        CancellationToken cancellationToken, TimeSpan offset = default)
    {
        DateTime startedAt = StartedAtUtc + offset;
        var started = await dispatcher.Send(
            new StartRunCommand(characterId, kind, startedAt, 0, "Sanctum", 30000142, null), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, startedAt.AddMinutes(15), startedAt.AddMinutes(16),
            [], [], [], []), cancellationToken);
    }

    /// <summary>The control tree as the operator sees it docked: the host lifts the window's content out and
    /// re-parents it (same as RunsVisualCorrectionsTests), so input is driven against that content, not a window
    /// that is never shown.</summary>
    private static async Task<Presented> _PresentAsync(TestClientInstance instance, double width,
        CancellationToken cancellationToken, double windowHeight = 1400)
    {
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        // Two characters, two run kinds: both the TYPES and CHARACTERS blocks get more than one tile, which is what
        // a solo (alt-click) needs to actually prove something.
        await _SaveAsync(dispatcher, 90000001, ActivityKind.Mining, cancellationToken);
        await _SaveAsync(dispatcher, 90000002, ActivityKind.Abyssal, cancellationToken, TimeSpan.FromHours(1));

        var viewModel = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services,
            Crew, runClock: false, paneReadDelay: TimeSpan.Zero);
        await viewModel.LoadAsync(cancellationToken);

        var window = new RunsWindow(viewModel) { Width = width, Height = windowHeight };
        var display = new FakeDisplay();
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(window, "RUNS", "runs", "runs");

        var content = (Control)Assert.Single(display.HostTabs).Content!;
        var root = new Window { Width = width, Height = windowHeight, Content = content };
        root.Show();
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return new Presented(root, content, viewModel);
    }

    private static Button _TileButton(Presented presented, RunFilterBlockViewModel block, string tileName)
    {
        Border filterBlock = presented.Content.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("filterblock") && ReferenceEquals(b.DataContext, block));
        return filterBlock.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("tile") && b.DataContext is RunFilterTileViewModel tile && tile.Name == tileName);
    }

    private static Point _CentreOf(Presented presented, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), presented.Root)
        ?? throw new InvalidOperationException("the tile is not in the tree");

    private static void _Click(Presented presented, Control tile, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Point point = _CentreOf(presented, tile);
        presented.Root.MouseDown(point, MouseButton.Left, modifiers);
        presented.Root.MouseUp(point, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData(1920d)]
    [InlineData(1200d)]
    [InlineData(975d)]
    [InlineData(630d)]
    public async Task Click_TogglesTheTileOff_AndClickingAgainTurnsItBackOn(double width)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        Presented presented = await _PresentAsync(instance, width, cancellationToken);

        RunFilterTileViewModel mining = presented.ViewModel.TypeFilter.Tiles.Single(tile => tile.Name == "Mining");
        Assert.True(mining.IsOn);

        _Click(presented, _TileButton(presented, presented.ViewModel.TypeFilter, "Mining"));
        Assert.False(mining.IsOn);
        Assert.Equal("1 of 2", presented.ViewModel.TypeFilter.SummaryText);

        _Click(presented, _TileButton(presented, presented.ViewModel.TypeFilter, "Mining"));
        Assert.True(mining.IsOn);
        Assert.Null(presented.ViewModel.TypeFilter.SummaryText);
    }

    [AvaloniaFact]
    public async Task AltClick_SolosTheTile_EveryOtherTileInTheBlockGoesOff()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        Presented presented = await _PresentAsync(instance, 1920, cancellationToken);

        _Click(presented, _TileButton(presented, presented.ViewModel.CharacterFilter, "Ra Vinter"),
            RawInputModifiers.Alt);

        Assert.True(presented.ViewModel.CharacterFilter.Tiles.Single(tile => tile.Name == "Ra Vinter").IsOn);
        Assert.False(presented.ViewModel.CharacterFilter.Tiles.Single(tile => tile.Name == "Kav Orn").IsOn);
        Assert.Equal("1 of 2", presented.ViewModel.CharacterFilter.SummaryText);

        presented.ViewModel.CharacterFilter.ShowAllCommand.Execute(null);
        Assert.All(presented.ViewModel.CharacterFilter.Tiles, tile => Assert.True(tile.IsOn));
    }

    [AvaloniaFact]
    public async Task Space_TogglesTheFocusedTile()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        Presented presented = await _PresentAsync(instance, 1920, cancellationToken);

        Button tile = _TileButton(presented, presented.ViewModel.TypeFilter, "Abyssal");
        RunFilterTileViewModel abyssal = presented.ViewModel.TypeFilter.Tiles.Single(t => t.Name == "Abyssal");
        Assert.True(tile.Focus());
        Dispatcher.UIThread.RunJobs();

        presented.Root.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, keySymbol: null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(abyssal.IsOn);
    }
}
