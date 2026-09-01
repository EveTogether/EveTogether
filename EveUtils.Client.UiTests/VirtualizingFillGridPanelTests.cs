using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Controls;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-116. <see cref="VirtualizingFillGridPanel"/> is <see cref="FillGridPanel"/> that only builds the cards you can
/// see. The grid it draws is the same grid — the column arithmetic is shared, and <see cref="FillGridPanelTests"/>
/// still owns it — so what is pinned down here is the virtualising half: how much gets built, that scrolling brings
/// the next rows in, that the scroller still knows how tall the whole grid is, and that a card's visual survives a
/// change of page instead of being built again. That last one is the ticket: a pool that hands nothing back is
/// silently exactly as slow as no pool at all, and nothing about the picture on screen says so.
/// </summary>
public class VirtualizingFillGridPanelTests
{
    private const double ItemHeight = 100;
    private const double RowSpacing = 10;

    private sealed class Row(int number)
    {
        public int Number { get; } = number;
    }

    /// <summary>A scroller over a virtualising grid, laid out — the shape the fit browser puts it in: the
    /// ScrollViewer around the ItemsControl, not inside its template.</summary>
    private static (Window Window, ScrollViewer Scroller, VirtualizingFillGridPanel Panel, ObservableCollection<Row> Items)
        Grid(double width, double height, int items, double minItemWidth = 200)
    {
        var rows = new ObservableCollection<Row>(Enumerable.Range(0, items).Select(i => new Row(i)));

        var control = new ItemsControl
        {
            ItemsSource = rows,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingFillGridPanel
            {
                MinItemWidth = minItemWidth,
                ColumnSpacing = 10,
                RowSpacing = RowSpacing
            }),
            ItemTemplate = new FuncDataTemplate<Row>((_, _) =>
                new Border { Height = ItemHeight, Child = new TextBlock() }, supportsRecycling: true)
        };

        var scroller = new ScrollViewer { Content = control };
        var window = new Window { Width = width, Height = height, Content = scroller };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var panel = (VirtualizingFillGridPanel)control.ItemsPanelRoot!;
        return (window, scroller, panel, rows);
    }

    /// <summary>The cards standing on screen. The pool's containers stay children of the panel, hidden.</summary>
    private static Control[] Realized(VirtualizingFillGridPanel panel) =>
        panel.Children.Where(c => c.IsVisible).ToArray();

    /// <summary>
    /// The point of the panel: a thousand items do not become a thousand visuals. What is realised is what the
    /// viewport touches plus one row either side — the cache row is what keeps a small scroll from having to build
    /// anything before it can draw.
    /// </summary>
    [AvaloniaFact]
    public void BuildsTheRowsTheViewportTouches_AndOneRowEitherSide()
    {
        // 1000 wide → 4 columns of 200+; 460 tall → 4 rows of 110 fit, and the fourth is cut.
        var (window, _, panel, _) = Grid(width: 1000, height: 460, items: 1000);

        var realized = Realized(panel);
        var rowsBuilt = realized.Select(c => Math.Round(c.Bounds.Y)).Distinct().Count();

        Assert.InRange(rowsBuilt, 4, 7);              // 4 on screen + the cache row below; never the whole list
        Assert.InRange(realized.Length, 16, 28);
        Assert.True(realized.Length < 100, $"{realized.Length} of 1000 cards were built — that is not virtualising");

        window.Close();
    }

    /// <summary>The scroller has to know how tall the whole grid is, not how tall the built part is: the extent is
    /// every row, or the scrollbar would claim the list is a screenful long and there would be no way down.</summary>
    [AvaloniaFact]
    public void TheScrollExtentCoversEveryRow_NotJustTheOnesThatWereBuilt()
    {
        var (window, scroller, panel, _) = Grid(width: 1000, height: 460, items: 1000);

        int columns = Realized(panel).Count(c => Math.Abs(c.Bounds.Y) < 0.5);
        int rows = (1000 + columns - 1) / columns;

        Assert.Equal(rows * (ItemHeight + RowSpacing) - RowSpacing, scroller.Extent.Height, 1);
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height * 10, "the grid would not scroll at all");

        window.Close();
    }

    /// <summary>Scrolling down builds what comes into view and hands back what left it, so the count stays flat
    /// however far down the list you go.</summary>
    [AvaloniaFact]
    public void ScrollingBringsTheNextRowsIn_AndLetsGoOfTheOnesBehind()
    {
        var (window, scroller, panel, _) = Grid(width: 1000, height: 460, items: 1000);

        var atTop = Realized(panel).Length;
        var firstRowNumbers = Realized(panel).Select(c => ((Row)c.DataContext!).Number).ToHashSet();

        scroller.Offset = new Vector(0, 5000);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var downThere = Realized(panel);
        var numbers = downThere.Select(c => ((Row)c.DataContext!).Number).ToHashSet();

        Assert.True(numbers.Min() > 100, $"scrolled 5000px and the first card is still #{numbers.Min()}");
        Assert.Empty(numbers.Intersect(firstRowNumbers));
        Assert.InRange(downThere.Length, atTop - 4, atTop + 4);

        window.Close();
    }

    /// <summary>
    /// The whole point of the pool, asserted as identity rather than as a stopwatch. Replacing every item — what a
    /// page turn does — has to put the SAME visuals back with different data on them. When this regressed (the
    /// container was cleared on its way into the pool, which throws the templated child away) every measurement of
    /// the grid still looked right and the page still cost 250 ms to put up.
    /// </summary>
    [AvaloniaFact]
    public void ChangingEveryItem_PutsTheSameVisualsBackWithNewDataOnThem()
    {
        var (window, _, panel, items) = Grid(width: 1000, height: 460, items: 40);

        var before = Realized(panel).ToList();
        var beforeChildren = before.Select(c => c.GetVisualDescendants().OfType<Border>().First()).ToList();

        // A different page: the same number of rows, none of them the ones that were there.
        items.Clear();
        foreach (var row in Enumerable.Range(100, 40).Select(i => new Row(i))) items.Add(row);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var after = Realized(panel).ToList();
        var afterChildren = after.Select(c => c.GetVisualDescendants().OfType<Border>().First()).ToList();

        Assert.NotEmpty(after);
        Assert.All(after, c => Assert.Contains(c, before));
        Assert.All(afterChildren, c => Assert.Contains(c, beforeChildren));
        Assert.All(after, c => Assert.True(((Row)c.DataContext!).Number >= 100,
            "a recycled container kept the row it had before"));

        window.Close();
    }

    /// <summary>A narrower window means fewer columns, which moves every card to a different slot: the panel hands
    /// everything back to the pool and re-slots it. The grid has to come out right anyway — filled to the edge, at
    /// the new column count.</summary>
    [AvaloniaFact]
    public void AChangeOfColumnCount_ReslotsEveryCard()
    {
        var (window, _, panel, _) = Grid(width: 1000, height: 460, items: 200);
        Assert.Equal(4, Realized(panel).Count(c => Math.Abs(c.Bounds.Y) < 0.5));

        window.Width = 500;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var cards = Realized(panel).Select(c => c.Bounds).ToArray();
        Assert.Equal(2, cards.Count(c => Math.Abs(c.Y) < 0.5));
        Assert.Equal(panel.Bounds.Width, cards.Max(c => c.Right), 1);

        window.Close();
    }

    /// <summary>ET-108's third lesson, which this panel inherits: an ArrangeOverride that hands back less than the
    /// rect it was given is CENTRED in the remainder, because Avalonia treats VerticalAlignment.Stretch on the same
    /// branch as Center. A grid that does not fill its viewport has to start at the top, not halfway down.</summary>
    [AvaloniaFact]
    public void ShortContentStaysAtTheTop_RatherThanBeingCentred()
    {
        var (window, _, panel, _) = Grid(width: 1000, height: 600, items: 2);

        Assert.Equal(0, Realized(panel).Min(c => c.Bounds.Y), 1);
        Assert.Equal(0, panel.Bounds.Y, 1);

        window.Close();
    }

    /// <summary>An empty list is not a crash and not a grid: nothing built, nothing reserved.</summary>
    [AvaloniaFact]
    public void AnEmptyListBuildsNothing()
    {
        var (window, _, panel, items) = Grid(width: 1000, height: 460, items: 20);
        Assert.NotEmpty(Realized(panel));

        items.Clear();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Empty(Realized(panel));
        Assert.Equal(0, panel.DesiredSize.Height, 1);

        window.Close();
    }

    /// <summary>The grid still fills the width it is given — the shared arithmetic of ET-108, checked once through
    /// this panel so a virtualising grid cannot quietly leave the strip of whitespace back on the right.</summary>
    [AvaloniaTheory]
    [InlineData(1000)]
    [InlineData(720)]
    [InlineData(420)]
    public void FillsTheWidth_LeavingNoStripOnTheRight(double width)
    {
        var (window, _, panel, _) = Grid(width, height: 460, items: 200);

        var cards = Realized(panel).Select(c => c.Bounds).ToArray();
        Assert.NotEmpty(cards);
        Assert.Equal(panel.Bounds.Width, cards.Max(c => c.Right), 1);
        Assert.All(cards, c => Assert.True(c.Width >= Math.Min(panel.Bounds.Width, 200) - 1,
            $"a card fell below the minimum in a panel of {panel.Bounds.Width}: {c.Width}"));

        window.Close();
    }
}
