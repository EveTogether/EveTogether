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
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-303: the band above the runs list at every width, read off <c>.Bounds</c>. Jithran's three screenshots (≈1200,
/// 975, 630) showed the month navigation running through the totals, the strip taking the whole width with an empty
/// column under it, and CHARACTERS half out of the window. The figures are the widest his own screenshot showed —
/// "185 activities · 27:46:11 flown · +2.35B ISK net" and "PUBLISH 175 LOCAL" — and his six characters and seven
/// types, so every piece is as wide as it gets in use.
/// </summary>
public sealed class RunsHeaderReflowTests
{
    private static readonly DateTime Evening = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<Character> Crew =
    [
        new("Jithran", 90000001), new("Abnoba Auscent", 90000002), new("ColdSprockets", 90000003),
        new("Lyra Custos", 90000004), new("Noahmarr", 90000005), new("Kaelen Voss", 90000006)
    ];

    private static readonly string[] Types =
        ["Site", "Combat Site", "Data Site", "Mission run", "Mining", "Homefront", "Abyssal"];

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    private sealed record Presented(Window Root, Control Content, RunsOverviewViewModel ViewModel);

    /// <summary>Pure arithmetic, no controls: which arrangement each measured width gets. Wide keeps RO-3's 400 px
    /// strip; medium narrows the strip first so the filters stay beside it; only below that do the tiles lose their
    /// names; and only below that does anything stack.</summary>
    [Theory]
    [InlineData(1892, RunsBandKind.Beside, 400, false)]   // 1920
    [InlineData(1172, RunsBandKind.Beside, 376, false)]   // 1200: strip narrows, both blocks keep two columns
    [InlineData(947, RunsBandKind.Beside, 400, false)]    // 975: one named column per block
    [InlineData(602, RunsBandKind.Beside, 346, true)]     // 630: compact tiles, still beside the strip
    [InlineData(392, RunsBandKind.Stacked, 392, true)]    // 420, the window's own MinWidth
    public void Band_PicksTheFirstArrangementThatFits(double bandWidth, RunsBandKind kind, double strip, bool compact)
    {
        RunsBandLayout layout = RunsLayout.Band(bandWidth);

        Assert.Equal(kind, layout.Kind);
        Assert.Equal(strip, layout.StripWidth);
        Assert.Equal(compact, layout.CompactTiles);
    }

    /// <summary>No two pieces of the range line share a pixel and none leaves the content root, at every width the
    /// ticket names and at the window's own minimum. Counter-proof: before ET-303 the navigation and the totals
    /// shared a Grid cell below 1200, and at 1100 the totals started at x=14 on top of ◀.</summary>
    [AvaloniaTheory]
    [InlineData(1920d)]
    [InlineData(1400d)]
    [InlineData(1200d)]
    [InlineData(1100d)]
    [InlineData(975d)]
    [InlineData(800d)]
    [InlineData(630d)]
    [InlineData(420d)]
    public async Task RangeLine_NoTwoPiecesOverlap_AndAllStayInside(double width)
    {
        using var instance = TestClientInstance.Create();
        Presented presented = await _PresentAsync(instance, width, TestContext.Current.CancellationToken);

        List<(string Name, Rect Rect)> pieces = [.. new[]
            {
                "RangePrevious", "RangeTitle", "RangeNext", "RangeFigures", "RangeJoint", "RangeIsk",
                "PublishViewButton", "SummaryToggle"
            }
            .Select(name => (name, _Rect(presented, _Named<Control>(presented, name))))
            .Where(piece => piece.Item2.Width > 0 && piece.Item2.Height > 0)];

        Assert.Contains(pieces, piece => piece.Name == "RangeFigures");
        Assert.Contains(pieces, piece => piece.Name == "PublishViewButton");
        double right = presented.Content.Bounds.Width;
        foreach ((string name, Rect rect) in pieces)
            Assert.True(rect.X >= -0.5 && rect.Right <= right + 0.5, $"{name} {rect} leaves 0..{right} at {width}");

        for (int i = 0; i < pieces.Count; i++)
            for (int j = i + 1; j < pieces.Count; j++)
                Assert.False(pieces[i].Rect.Intersects(pieces[j].Rect),
                    $"{pieces[i].Name} {pieces[i].Rect} overlaps {pieces[j].Name} {pieces[j].Rect} at {width}");
    }

    /// <summary>The band at every width: each filter block wholly inside the content root and clear of the strip and
    /// of the other block, every tile and the "n of m · show all" line inside its own block, the strip's cells inside
    /// the strip, and the header under its height bound with the list starting below it. Counter-proof: before ET-303,
    /// at 630 both blocks sat in a 73 px leftover right of an empty 400 px column, and at 420 they started at x=442.</summary>
    [AvaloniaTheory]
    [InlineData(1920d, 240d)]
    [InlineData(1400d, 240d)]
    [InlineData(1200d, 240d)]
    [InlineData(1100d, 240d)]
    [InlineData(975d, 310d)]
    [InlineData(800d, 310d)]
    [InlineData(630d, 310d)]
    [InlineData(420d, 460d)]
    public async Task Band_KeepsEveryBlockInside_AndTheHeaderUnderItsBound(double width, double headerBound)
    {
        using var instance = TestClientInstance.Create();
        Presented presented = await _PresentAsync(instance, width, TestContext.Current.CancellationToken);
        // Something off in both blocks, so the "n of m · show all" line is on screen and must fit too.
        presented.ViewModel.TypeFilter.SummaryText = "6 of 7";
        presented.ViewModel.CharacterFilter.SummaryText = "5 of 6";
        _Settle(presented);

        double right = presented.Content.Bounds.Width;
        var strip = _Named<RunsActivityStrip>(presented, "ActivityStrip");
        Rect stripRect = _Rect(presented, strip);
        Rect band = _Rect(presented, _Named<Grid>(presented, "Band"));
        Assert.True(_Within(stripRect, band), $"strip {stripRect} leaves the band {band} at {width}");
        foreach (Control inner in strip.GetVisualDescendants().OfType<Control>()
                     .Where(c => c.IsVisible && (c.Classes.Contains("stripcell") || c.Classes.Contains("weekseg")
                                                || c.Classes.Contains("shade"))))
            Assert.True(_Within(_Rect(presented, inner), stripRect),
                $"{inner.Classes} {_Rect(presented, inner)} leaves the strip {stripRect} at {width}");

        Rect[] blocks = [.. new[] { "TypeFilterBlock", "CharacterFilterBlock" }.Select(name =>
        {
            var block = _Named<Border>(presented, name);
            Rect rect = _Rect(presented, block);
            Assert.True(rect.X >= -0.5 && rect.Right <= right + 0.5, $"{name} {rect} leaves 0..{right} at {width}");
            Assert.False(rect.Intersects(stripRect), $"{name} {rect} overlaps the strip {stripRect} at {width}");

            List<Control> inside = [.. block.GetVisualDescendants().OfType<Control>()
                .Where(c => c.IsVisible && (c.Classes.Contains("tile") || c.Classes.Contains("filterstate")))];
            Assert.Equal(1, inside.Count(c => c.Classes.Contains("filterstate")));
            foreach (Control control in inside)
                Assert.True(_Within(_Rect(presented, control), rect),
                    $"{name}: {control.Classes} {_Rect(presented, control)} leaves its block {rect} at {width}");
            return rect;
        })];
        Assert.False(blocks[0].Intersects(blocks[1]), $"TYPES {blocks[0]} overlaps CHARACTERS {blocks[1]} at {width}");

        Border header = _HeaderOf(presented);
        Rect headerRect = _Rect(presented, header);
        Rect list = _Rect(presented, _Named<ListBox>(presented, "ActivityList"));
        Assert.True(headerRect.Height <= headerBound, $"header is {headerRect.Height} px tall at {width}, over {headerBound}");
        Assert.True(list.Top >= headerRect.Bottom - 0.5, $"the list starts at {list.Top}, above the header's {headerRect.Bottom}");
    }

    private static bool _Within(Rect inner, Rect outer) =>
        inner.X >= outer.X - 0.5 && inner.Y >= outer.Y - 0.5
        && inner.Right <= outer.Right + 0.5 && inner.Bottom <= outer.Bottom + 0.5;

    /// <summary>The Border around the range line and the band: the header this ticket bounds.</summary>
    private static Border _HeaderOf(Presented presented) =>
        _Named<Grid>(presented, "Band").GetVisualAncestors().OfType<Border>().First();

    private static Rect _Rect(Presented presented, Control control)
    {
        Point origin = control.TranslatePoint(new Point(0, 0), presented.Content) ?? default;
        return new Rect(origin, control.Bounds.Size);
    }

    private static T _Named<T>(Presented presented, string name) where T : Control =>
        presented.Content.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new XunitException($"no {typeof(T).Name} named {name}");

    private static void _Settle(Presented presented)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            presented.Root.UpdateLayout();
        }
    }

    private static async Task<Presented> _PresentAsync(TestClientInstance instance, double width,
        CancellationToken cancellationToken)
    {
        var dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        foreach (Character character in Crew)
        {
            Result<Guid> started = await dispatcher.Send(new StartRunCommand(character.EsiCharacterId!.Value,
                ActivityKind.Site, Evening, 1234, "Homefront", 30000142, null), cancellationToken);
            await dispatcher.Send(new SaveRunCommand(started.Value, Evening.AddMinutes(15), Evening.AddMinutes(16),
                [], [], [], []), cancellationToken);
        }

        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        var viewModel = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services,
            Crew, runClock: false, paneReadDelay: TimeSpan.Zero);
        await viewModel.LoadAsync(cancellationToken);

        var window = new RunsWindow(viewModel) { Width = width, Height = 1000 };
        var display = new FakeDisplay();
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(window, "RUNS", "runs", "runs");

        var content = (Control)Assert.Single(display.HostTabs).Content!;
        var root = new Window { Width = width, Height = 1000, Content = content };
        root.Show();
        var presented = new Presented(root, content, viewModel);
        _Settle(presented);

        // The widest figures Jithran's screenshot showed. PUBLISH needs a coupled server to show on its own; its
        // width is what matters here, so it is shown directly.
        viewModel.RangeTitleText = "SEPTEMBER 2026";
        viewModel.RangeCountText = "185 activities";
        viewModel.RangeFlownText = "27:46:11 flown";
        viewModel.RangeNetText = "+2.35B ISK net";
        var publish = _Named<Button>(presented, "PublishViewButton");
        publish.IsVisible = true;
        publish.Content = "PUBLISH 175 LOCAL";
        viewModel.TypeFilter.Tiles.Clear();
        foreach (string type in Types)
            viewModel.TypeFilter.Tiles.Add(new RunFilterTileViewModel(type, type, MaterialIconKind.Sword, null,
                _ => { }, _ => { }) { Count = 185 });
        _Settle(presented);
        return presented;
    }
}
