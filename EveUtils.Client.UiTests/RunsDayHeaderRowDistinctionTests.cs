using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Theming;
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
/// ET-307: a day header and a run row read as nearly the same thing — same caret glyph at the same x, background
/// washes close enough that a selected run sitting right above the next day looked like it belonged to that header.
/// Jithran: <i>"vooral tussen de run die nu gehighlight staat en tuesday is nu heel onduidelijk."</i> Pins the two
/// numeric acceptance criteria: a run row indents past its day header and never shares the header's caret x
/// (<see cref="RunRow_IndentsPastTheDayHeader_AndItsCaretNeverSharesTheDayHeaderCaretsX"/>), and the day header's
/// own band reads as a measurably different colour from both a normal and a selected row, in every theme, with
/// enough contrast left for its own text (<see cref="DayHeaderBand_MeasurablyDiffersFromNormalAndSelectedRow_InEveryTheme"/>).
/// </summary>
public sealed class RunsDayHeaderRowDistinctionTests
{
    private static readonly DateTime Evening = new(2026, 9, 13, 19, 0, 0, DateTimeKind.Utc);

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    private sealed record Presented(Window Root, Control Content, RunsOverviewViewModel ViewModel);

    /// <summary>Two pilots on the same site/signature fold into one activity row with a crew stack, so the row's
    /// own caret — normally hidden on a solo run — realises next to the day header's, at 1200 px (comfortably
    /// inside the ET-307 acceptance widths of 1920/1200/975/630).</summary>
    [AvaloniaFact]
    public async Task RunRow_IndentsPastTheDayHeader_AndItsCaretNeverSharesTheDayHeaderCaretsX()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        ICqrsDispatcher dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        await _SaveRunAsync(dispatcher, 90000001, cancellationToken);
        await _SaveRunAsync(dispatcher, 90000002, cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);

        Presented presented = await _PresentAsync(instance, 1200, cancellationToken);
        RunsDayViewModel day = presented.ViewModel.Tabs[0].Days.Single();
        ActivityOverviewRowViewModel row = Assert.Single(day.Rows);
        Assert.True(row.HasCrewStack);

        var list = _Named<ListBox>(presented, "ActivityList");
        Control dayContainer = list.ContainerFromItem(day) ?? throw new Xunit.Sdk.XunitException("day header not realised");
        Control rowContainer = list.ContainerFromItem(row) ?? throw new Xunit.Sdk.XunitException("row not realised");

        Grid dayTotals = dayContainer.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "DayTotals");
        Grid rowGrid = rowContainer.GetVisualDescendants().OfType<Grid>().First(g => g.ColumnDefinitions.Count == 7);
        Button rowCaretBtn = rowContainer.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("caretbtn"));

        // The caret's own slot, not the glyph's centred TextBlock inside it (a smaller/dimmer rowcaret glyph
        // centres differently than the day header's and would make this assert font-metric-dependent): the day
        // header's caret sits in DayTotals's own first (18 px) column, so DayTotals's left edge IS that column's
        // left edge; the row's caret is its own fixed-width Button, so its Bounds.X is exactly its slot's edge.
        double dayCaretSlotX = _Rect(presented, dayTotals).X;
        double rowGridX = _Rect(presented, rowGrid).X;
        Assert.True(rowGridX > dayCaretSlotX + 4,
            $"row content at {rowGridX} is not indented past the day header's own {dayCaretSlotX}");

        double rowCaretSlotX = _Rect(presented, rowCaretBtn).X;
        Assert.True(Math.Abs(dayCaretSlotX - rowCaretSlotX) >= 4,
            $"day caret's slot at {dayCaretSlotX} and the row caret's slot at {rowCaretSlotX} sit at nearly the same x");
    }

    /// <summary>Composites <c>BgDayHeadBrush</c>/<c>BgRowSelectedBrush</c>/<c>BgRowHoverBrush</c> over the panel the
    /// list actually sits on (<c>BgPanelBrush</c>) and measures the day header's band against both row states with
    /// the same RGB-distance and WCAG-contrast math used to pick the values (ET-307 plan). The bar it must clear:
    /// read at least as far from a normal and a selected row as hover already reads from selected today — a gap the
    /// shipped app already treats as visually distinct — and keep TextBrush's 4.5:1 floor on its own band.</summary>
    [AvaloniaTheory]
    [InlineData(FactionTheme.Caldari)]
    [InlineData(FactionTheme.Amarr)]
    [InlineData(FactionTheme.Gallente)]
    [InlineData(FactionTheme.Minmatar)]
    public void DayHeaderBand_MeasurablyDiffersFromNormalAndSelectedRow_InEveryTheme(FactionTheme faction)
    {
        using var instance = TestClientInstance.Create();
        var theme = instance.Services.GetRequiredService<IThemeService>();
        try
        {
            theme.Apply(faction);

            Color panel = _Resource("BgPanelBrush");
            Color dayHead = _CompositeOverPanel(_Resource("BgDayHeadBrush"), panel);
            Color selected = _CompositeOverPanel(_Resource("BgRowSelectedBrush"), panel);
            Color hover = _CompositeOverPanel(_Resource("BgRowHoverBrush"), panel);
            Color textBrush = _Resource("TextBrush");

            double normalGap = _Distance(dayHead, panel);
            double selectedGap = _Distance(dayHead, selected);
            double hoverVsSelectedGap = _Distance(hover, selected);

            Assert.True(normalGap > hoverVsSelectedGap,
                $"[{faction}] day header vs normal row = {normalGap:F1}, weaker than hover-vs-selected's own {hoverVsSelectedGap:F1}");
            Assert.True(selectedGap > hoverVsSelectedGap * 0.8,
                $"[{faction}] day header vs selected row = {selectedGap:F1}, not clearly apart from hover-vs-selected's own {hoverVsSelectedGap:F1}");

            double textContrast = _Contrast(textBrush, dayHead);
            Assert.True(textContrast >= 4.5,
                $"[{faction}] TextBrush on the day header measured {textContrast:F2}:1, under the 4.5 the project holds text to");
        }
        finally
        {
            theme.Apply(FactionTheme.Gallente);
        }
    }

    private static Color _Resource(string key)
    {
        if (!Application.Current!.TryGetResource(key, null, out object? value))
            throw new Xunit.Sdk.XunitException($"resource {key} not found");
        return value switch
        {
            Color c => c,
            ISolidColorBrush b => b.Color,
            _ => throw new Xunit.Sdk.XunitException($"resource {key} is a {value?.GetType()}"),
        };
    }

    private static Color _CompositeOverPanel(Color fg, Color bg)
    {
        double af = fg.A / 255.0;
        byte R = (byte)Math.Round(fg.R * af + bg.R * (1 - af));
        byte G = (byte)Math.Round(fg.G * af + bg.G * (1 - af));
        byte B = (byte)Math.Round(fg.B * af + bg.B * (1 - af));
        return Color.FromRgb(R, G, B);
    }

    private static double _Distance(Color a, Color b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    private static double _RelLum(Color c)
    {
        static double Lin(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double _Contrast(Color a, Color b)
    {
        double l1 = _RelLum(a), l2 = _RelLum(b);
        if (l2 > l1) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
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

    /// <summary>Two runs, same site/signature: the shape that folds into one activity with a crew stack (mirrors
    /// RunsCrewExternalCharacterTests's own fixture).</summary>
    private static async Task _SaveRunAsync(ICqrsDispatcher dispatcher, long characterId, CancellationToken cancellationToken)
    {
        Result<Guid> started = await dispatcher.Send(new StartRunCommand(characterId, ActivityKind.Site, Evening,
            1234, "Homefront", 30000142, "HF-QTY3"), cancellationToken);
        await dispatcher.Send(new SaveRunCommand(started.Value, Evening.AddMinutes(15), Evening.AddMinutes(16),
            [], [], [], []), cancellationToken);
    }

    private static async Task<Presented> _PresentAsync(
        TestClientInstance instance, double width, CancellationToken cancellationToken, double windowHeight = 1400)
    {
        var dispatcher = instance.Services.GetRequiredService<ICqrsDispatcher>();
        var viewModel = new RunsOverviewViewModel(dispatcher, new RecordingDialogService(), instance.Services,
            [new Character("Jithran", 90000001)], runClock: false, paneReadDelay: TimeSpan.Zero);
        await viewModel.LoadAsync(cancellationToken);
        foreach (RunsDayViewModel day in viewModel.Tabs[0].Days)
            day.IsExpanded = true;

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
