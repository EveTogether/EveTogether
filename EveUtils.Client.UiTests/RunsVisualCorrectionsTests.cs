using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels.Runs;
using EveUtils.Client.Views;
using EveUtils.Client.Views.Runs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-302: a visual-correction round on the finished D v2 runs overview, read off <c>.Bounds</c> rather than a
/// screenshot — Jithran, 16 September 2026: <i>"kijk dit zelf nu nog even goed na nu alles erin zit."</i> One test
/// per defect his screenshot named: the TYPES/CHARACTERS tiles not sharing a width (ET-293), the activity strip
/// clipped past its own column (ET-292), the ISK bar overlapping the range line (ET-292/ET-294), and a day's first
/// row disappearing under the sticky header (ET-290/ET-291).
/// </summary>
public sealed class RunsVisualCorrectionsTests
{
    private static readonly DateTime Evening = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<Character> Crew =
    [
        new("Jithran", 90000001), new("Abnoba Auscent", 90000002),
        new("ColdSprockets", 90000003), new("Lyra Custos", 90000004)
    ];

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    private sealed record Presented(Window Root, Control Content, RunsOverviewViewModel ViewModel);

    /// <summary>ET-293: within TYPES and within CHARACTERS every tile shares one column width, chosen from the
    /// block's own widest name — not each tile's own content. Counter-proof: a tile that sizes itself to its own
    /// text (the pre-ET-302 shape) gives "Combat Site" a wider box than "Site" in the same column.</summary>
    [AvaloniaFact]
    public async Task FilterTiles_ShareOneWidthPerBlock()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        Presented presented = await _PresentAsync(instance, 1920, cancellationToken);

        // Names of very different natural widths, in both blocks — the exact shapes Jithran's screenshot named.
        _ReplaceTiles(presented.ViewModel.TypeFilter, [
            ("Site", 7), ("Combat Site", 97), ("Data Site", 5), ("Mission run", 14),
            ("Mining", 1), ("Homefront", 51), ("Abyssal", 10)
        ]);
        _ReplaceTiles(presented.ViewModel.CharacterFilter, [
            ("Abnoba Auscent", 38), ("Jithran", 185), ("ColdSprockets", 36), ("Lyra Custos", 36)
        ]);
        Dispatcher.UIThread.RunJobs();
        presented.Root.UpdateLayout();

        List<double> typeWidths = _TileWidths(presented, presented.ViewModel.TypeFilter);
        List<double> characterWidths = _TileWidths(presented, presented.ViewModel.CharacterFilter);
        Assert.Equal(7, typeWidths.Count);
        Assert.Equal(4, characterWidths.Count);

        // Every tile in a block within a pixel of every other — layout rounding may snap column edges by less
        // than a device pixel, real per-content sizing would not.
        AssertAllWithin(typeWidths, 1.0, "TYPES");
        AssertAllWithin(characterWidths, 1.0, "CHARACTERS");
    }

    /// <summary>ET-292: at the wide layout the strip is handed a 400 px column and must fit inside it — twelve
    /// weeks of cells and the legend/shade toggle row all end at or before that column's own right edge, never
    /// past it into the TYPES block beside it.</summary>
    [AvaloniaFact]
    public async Task ActivityStrip_FitsWithinItsOwnColumn_AtEveryWidth()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();

        foreach (double width in new[] { 1920d, 1303d })
        {
            Presented presented = await _PresentAsync(instance, width, cancellationToken);

            var strip = _Named<RunsActivityStrip>(presented, "ActivityStrip");
            double stripRight = _Right(presented, strip);

            // Every realised cell, month label and week segment inside the strip — none of them may end past the
            // strip control's own right edge, which is ET-292's "runs past the right edge of its own block".
            List<Control> inner = strip.GetVisualDescendants().OfType<Control>()
                .Where(c => c.Classes.Contains("stripcell") || c.Classes.Contains("monthlabel")
                            || c.Classes.Contains("weekseg") || c.Classes.Contains("shade"))
                .ToList();
            Assert.NotEmpty(inner);
            foreach (Control cell in inner)
                Assert.True(_Right(presented, cell) <= stripRight + 0.5,
                    $"{cell.Classes} ends at {_Right(presented, cell)}, past the strip's own {stripRight}");

            // And the strip itself never spills past the band's own right edge in the docked width.
            Assert.True(strip.Bounds.Width <= 400.5, $"strip is {strip.Bounds.Width} px wide, wider than its 400 px column");
        }
    }

    /// <summary>ET-292/ET-294: the range line's title/actions on top and totals/ISK bar below (or beside it, wide)
    /// never overlap each other and never run past the content root's own right edge — checked at the three widths
    /// the ticket names.</summary>
    [AvaloniaTheory]
    [InlineData(1920d)]
    [InlineData(1303d)]
    [InlineData(758d)]
    public async Task RangeLine_ElementsNeverOverlap_AndStayInsideTheWindow(double width)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        await _SaveRunAsync(instance, 90000001, Evening, cancellationToken);
        Presented presented = await _PresentAsync(instance, width, cancellationToken);

        var rangeTitle = _Named<TextBlock>(presented, "RangeTitle");
        var rangeActions = _Named<StackPanel>(presented, "RangeActions");
        var rangeTotals = _Named<StackPanel>(presented, "RangeFigures");
        var rangeNet = _Named<TextBlock>(presented, "RangeNet");

        Rect titleRect = _Rect(presented, rangeTitle);
        Rect actionsRect = _Rect(presented, rangeActions);
        Rect totalsRect = _Rect(presented, rangeTotals);
        Rect netRect = _Rect(presented, rangeNet);

        // The title (◀ title ▶) never collides with SUMMARY/PUBLISH on the same line.
        Assert.False(titleRect.Intersects(actionsRect),
            $"title {titleRect} overlaps actions {actionsRect} at {width}");
        // The totals line (count · flown · bar · net ISK) never collides with the actions above it when the two
        // share a line (wide), and the ISK figure never runs past the content root.
        Assert.False(totalsRect.Intersects(actionsRect) && width >= RunsLayout.WideFrom,
            $"totals {totalsRect} overlaps actions {actionsRect} at {width}");
        Assert.True(netRect.Right <= presented.Content.Bounds.Width + 0.5,
            $"net ISK ends at {netRect.Right}, past the content root's {presented.Content.Bounds.Width} at {width}");
        Assert.True(totalsRect.Right <= presented.Content.Bounds.Width + 0.5,
            $"totals end at {totalsRect.Right}, past the content root's {presented.Content.Bounds.Width} at {width}");
    }

    /// <summary>ET-290/ET-291: once the day header has scrolled off and the sticky copy pins itself over the list,
    /// the list's own content never starts above the pin's own bottom edge — its own row now (ET-302), not an
    /// overlay sharing the list's row, so nothing the list draws can ever need space the pin already took.</summary>
    [AvaloniaFact]
    public async Task StickyDayHeader_NeverOverlapsTheListsOwnContent()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        for (int day = 0; day < 10; day++)
        {
            await _SaveRunAsync(instance, 90000001, Evening.AddDays(-day), cancellationToken);
            await _SaveRunAsync(instance, 90000002, Evening.AddDays(-day).AddHours(1), cancellationToken);
        }

        // Short on purpose: the list must hold less than all ten days at once, or nothing scrolls and the sticky
        // header never has a reason to pin.
        Presented presented = await _PresentAsync(instance, 1303, cancellationToken, windowHeight: 500);
        RunsOverviewViewModel viewModel = presented.ViewModel;
        foreach (RunsDayViewModel day in viewModel.Tabs[0].Days)
            day.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        presented.Root.UpdateLayout();

        var list = _Named<ListBox>(presented, "ActivityList");
        var sticky = _Named<Border>(presented, "StickyDay");
        Assert.True(list.Scroll is ScrollViewer);
        var scroll = (ScrollViewer)list.Scroll!;
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height,
            $"nothing to scroll: extent {scroll.Extent.Height} vs viewport {scroll.Viewport.Height}");

        // An arbitrary mid-scroll position — less than one row past the first day header — the same kind of
        // moment the screenshot was taken at, not a position anything special was done to reach.
        scroll.Offset = new Vector(0, 40);
        Dispatcher.UIThread.RunJobs();
        presented.Root.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.True(sticky.IsVisible, "the sticky header never pinned — nothing to check against");
        double stickyBottom = _Rect(presented, sticky).Bottom;
        double listTop = _Rect(presented, list).Top;
        Assert.True(listTop >= stickyBottom - 0.5,
            $"the list's own top {listTop} sits above the sticky header's bottom {stickyBottom}");
    }

    /// <summary>ET-385: a day picked in the strip scrolls its own header to the very top of the list
    /// (<c>RunsWindow._ScrollDayToTop</c>). The pin sits in a row of its own above the list since ET-302, so pinning
    /// then would draw that header twice; it pins only once the real one has scrolled above the top. Counter-proof:
    /// pinning on any scroll offset showed the picked day's header both above the list and as its first item.</summary>
    [AvaloniaFact]
    public async Task StickyDayHeader_APickedDayShowsItsOwnHeaderAtTheTopAndPinsOnlyOnceItScrollsAway()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        for (int day = 0; day < 15; day++)
        {
            await _SaveRunAsync(instance, 90000001, Evening.AddDays(-day), cancellationToken);
            await _SaveRunAsync(instance, 90000002, Evening.AddDays(-day).AddHours(1), cancellationToken);
        }

        Presented presented = await _PresentAsync(instance, 1303, cancellationToken, windowHeight: 500);
        RunsOverviewViewModel viewModel = presented.ViewModel;
        RunsTabViewModel tab = viewModel.Tabs[0];
        // Well below the newest day (the only one expanded by default, ET-199), so picking it is a real scroll,
        // not a no-op at the top of the list, yet with enough list under it to scroll a header away.
        RunsDayViewModel picked = tab.Days[6];
        DateOnly pickedDate = DateOnly.FromDateTime(picked.Day);

        viewModel.Strip.Cells.Single(cell => cell.Date == pickedDate).ClickCommand.Execute(null);
        await ActivityWindowHarness.WaitUntil(() => viewModel.RangeKind == RunsRangeKind.Day);
        // _ScrollDayToTop posts its own layout/scroll steps at Background priority; pump them through.
        for (int pump = 0; pump < 8; pump++)
        {
            Dispatcher.UIThread.RunJobs();
            presented.Root.UpdateLayout();
        }

        Assert.True(picked.IsExpanded);
        var list = _Named<ListBox>(presented, "ActivityList");
        var sticky = _Named<Border>(presented, "StickyDay");
        var scroll = (ScrollViewer)list.Scroll!;
        Assert.True(scroll.Offset.Y > 0.5, "the picked day never scrolled — nothing to check against");
        Control? header = list.ContainerFromItem(picked);
        Assert.NotNull(header);
        Assert.False(sticky.IsVisible, "the picked day's header is pinned above its own header at the top of the list");
        Assert.True(_Rect(presented, header!).Top >= _Rect(presented, list).Top - 0.5,
            "the picked day's own header is not at the top of the list");

        scroll.Offset = new Vector(0, scroll.Offset.Y + header!.Bounds.Height + 4);
        for (int pump = 0; pump < 4; pump++)
        {
            Dispatcher.UIThread.RunJobs();
            presented.Root.UpdateLayout();
        }

        Assert.True(sticky.IsVisible, "the header scrolled away but nothing pinned");
        Assert.Same(picked, _Named<ContentControl>(presented, "StickyDayContent").Content);
    }

    /// <summary>ET-305: the portrait (CHARACTERS) and the icon (TYPES) in a filter tile centre vertically on the
    /// tile, in both its normal form and ET-303's compact one — including a row well past <c>MinRowHeight</c>,
    /// which is what actually exposed this in Jithran's screenshot (ColumnFlowPanel stretches a row to use spare
    /// height beside the taller strip, RO-4). Counter-proof: before this fix neither style set VerticalAlignment,
    /// so both fell back to the Grid's own Stretch default (ET-293). A Bounds-only check would miss it for the
    /// portrait — Stretch makes its Bounds equal the row, so a Bounds-vs-tile centre compare is trivially true —
    /// which is why this also pins each glyph's own height to its natural size: stretched, <c>HexPortrait</c> still
    /// paints a fixed 20 px hex at its own top-left (ET-290's shared draw), so a Bounds taller than that natural
    /// size is exactly the bug, on screen as empty space under the portrait and none above it.</summary>
    [AvaloniaTheory]
    [InlineData(1920d, false)]
    [InlineData(630d, true)]
    public async Task FilterTileGlyph_IsVerticallyCentered_NormalAndCompact(double width, bool compact)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        await _SaveRunAsync(instance, 90000001, Evening, cancellationToken);
        Presented presented = await _PresentAsync(instance, width, cancellationToken);

        var band = _Named<Grid>(presented, "Band");
        Assert.Equal(compact, band.Classes.Contains("compact"));

        // Icon (16 px, its own explicit Height) and portrait (HexPortrait's 34:39 proportion at Size 20) each have
        // one correct natural height regardless of how tall the row around them stretches.
        List<(Control Glyph, Button Tile, double NaturalHeight)> glyphs = [.. presented.Content.GetVisualDescendants()
            .OfType<Button>()
            .Where(tile => tile.Classes.Contains("tile") && tile.IsVisible)
            .Select(tile => (Tile: tile, Glyph: tile.GetVisualDescendants().OfType<Control>().FirstOrDefault(c =>
                c.IsVisible && (c.Classes.Contains("tileicon") || c.Classes.Contains("tileportrait")))))
            .Where(pair => pair.Glyph is not null)
            .Select(pair => (pair.Glyph!, pair.Tile,
                NaturalHeight: pair.Glyph!.Classes.Contains("tileicon") ? 16.0 : System.Math.Round(20 * 39.0 / 34)))];

        Assert.NotEmpty(glyphs);
        Assert.Contains(glyphs, g => g.Glyph.Classes.Contains("tileportrait"));
        Assert.Contains(glyphs, g => g.Glyph.Classes.Contains("tileicon"));
        foreach ((Control glyph, Button tile, double naturalHeight) in glyphs)
        {
            Rect glyphRect = _Rect(presented, glyph);
            Rect tileRect = _Rect(presented, tile);
            Assert.True(System.Math.Abs(glyphRect.Height - naturalHeight) <= 1.0,
                $"{glyph.Classes} is {glyphRect.Height} px tall, not its natural {naturalHeight} — stretched to the row at {width}");
            double glyphCenter = glyphRect.Top + glyphRect.Height / 2;
            double tileCenter = tileRect.Top + tileRect.Height / 2;
            Assert.True(System.Math.Abs(glyphCenter - tileCenter) <= 1.0,
                $"{glyph.Classes} rect={glyphRect} centre {glyphCenter} vs tile rect={tileRect} centre {tileCenter} at {width}");
            Assert.True(glyphRect.Top >= tileRect.Top - 0.5 && glyphRect.Bottom <= tileRect.Bottom + 0.5,
                $"{glyph.Classes} {glyphRect} leaves the tile {tileRect} at {width}");
        }
    }

    private static void AssertAllWithin(IReadOnlyList<double> widths, double tolerance, string block)
    {
        double first = widths[0];
        foreach (double width in widths)
            Assert.True(System.Math.Abs(width - first) <= tolerance,
                $"{block} tile widths vary: {string.Join(", ", widths)}");
    }

    private static void _ReplaceTiles(RunFilterBlockViewModel block, (string Name, int Count)[] tiles)
    {
        block.Tiles.Clear();
        foreach ((string name, int count) in tiles)
            block.Tiles.Add(new RunFilterTileViewModel(name, name, null, null, _ => { }, _ => { }) { Count = count });
    }

    private static List<double> _TileWidths(Presented presented, RunFilterBlockViewModel block)
    {
        Border filterBlock = presented.Content.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("filterblock") && ReferenceEquals(b.DataContext, block));
        return [.. filterBlock.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("tile") && b.IsVisible)
            .Select(b => b.Bounds.Width)];
    }

    private static double _Right(Presented presented, Control control) => _Rect(presented, control).Right;

    private static Rect _Rect(Presented presented, Control control)
    {
        Point origin = control.TranslatePoint(new Point(0, 0), presented.Content) ?? default;
        return new Rect(origin, control.Bounds.Size);
    }

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
        TestClientInstance instance, double width, CancellationToken cancellationToken, double windowHeight = 1400)
    {
        var dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

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
}
