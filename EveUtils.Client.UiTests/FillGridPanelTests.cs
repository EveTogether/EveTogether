using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Controls;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-108. <see cref="FillGridPanel"/> is the panel behind a card grid that fills the width it is given: the item
/// width is a MINIMUM and the leftover is divided over the columns that fit, so no strip of whitespace is left on
/// the right. These are the panel's own rules, asserted on arranged bounds rather than on the screen that uses it —
/// the fit overview is meant to land on this same panel.
/// </summary>
public class FillGridPanelTests
{
    private const double Tolerance = 0.01;

    // A laid-out panel with n same-height children, measured and arranged at a given width — the two passes a real
    // host runs, so the assertions read the bounds a card actually gets.
    private static FillGridPanel Lay(double width, int items, double minItemWidth, double columnSpacing = 0,
        double rowSpacing = 0, double itemHeight = 100)
    {
        var panel = new FillGridPanel
        {
            MinItemWidth = minItemWidth,
            ColumnSpacing = columnSpacing,
            RowSpacing = rowSpacing,
        };

        for (var i = 0; i < items; i++)
            panel.Children.Add(new Border { Height = itemHeight });

        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
        return panel;
    }

    private static Rect[] Cards(Panel panel) => panel.Children.Select(c => c.Bounds).ToArray();

    /// <summary>
    /// The operator's own two numbers, which is what this ticket was opened on: at a minimum of 200 a container of
    /// 600 gives three columns of 200, and a container of 576 gives two of 288 — not two of 200 with 176 of white
    /// beside them. CSS's <c>repeat(auto-fill, minmax(200px, 1fr))</c>, arrived at here.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(600, 3, 200)]
    [InlineData(576, 2, 288)]
    public void DividesTheLeftoverOverTheColumnsThatFit(double width, int expectedColumns, double expectedItemWidth)
    {
        Rect[] cards = Cards(Lay(width, items: 6, minItemWidth: 200));

        Assert.Equal(expectedColumns, cards.Count(c => Math.Abs(c.Y - cards[0].Y) < Tolerance));
        Assert.All(cards, c => Assert.Equal(expectedItemWidth, c.Width, Tolerance));
    }

    /// <summary>
    /// The column count is <c>floor((available + spacing) / (min + spacing))</c>, so with the fleet-metrics card
    /// (318 minimum, 8 spacing) a column is added at exactly 644, 970 and 1296. One pixel below each of those the
    /// previous count still stands and its cards are at their widest.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(643, 1)]
    [InlineData(644, 2)]
    [InlineData(969, 2)]
    [InlineData(970, 3)]
    [InlineData(1295, 3)]
    [InlineData(1296, 4)]
    public void AddsAColumnExactlyWhereAnotherMinimumFits(double width, int expectedColumns)
    {
        Rect[] cards = Cards(Lay(width, items: 8, minItemWidth: 318, columnSpacing: 8));

        Assert.Equal(expectedColumns, cards.Count(c => Math.Abs(c.Y - cards[0].Y) < Tolerance));

        // A whole pixel of slack: an equal share is rarely a whole number and the edges are snapped, so a column may
        // sit up to a pixel under the minimum. Anything more would mean a column too many.
        Assert.All(cards, c => Assert.True(c.Width >= 318 - 1,
            $"a column fell below the minimum at {width}: {c.Width}"));
    }

    /// <summary>The complaint itself: whatever the width, the last column ends flush with the panel. A strip of
    /// whitespace on the right is the one thing this panel exists to remove.</summary>
    [AvaloniaTheory]
    [InlineData(490)]     // the window's MinWidth, minus its chrome — one column
    [InlineData(643)]     // the widest a card ever gets
    [InlineData(690)]     // the default 720-wide window
    [InlineData(970)]
    [InlineData(1370)]
    public void LeavesNoWhitespaceOnTheRight(double width)
    {
        FillGridPanel panel = Lay(width, items: 8, minItemWidth: 318, columnSpacing: 8);
        Rect[] cards = Cards(panel);

        Assert.Equal(width, cards.Max(c => c.Right), Tolerance);
        Assert.Equal(width, panel.Bounds.Width, Tolerance);
    }

    /// <summary>Below one minimum the single column takes the whole width rather than being clipped to a size
    /// nothing was designed for — this is the narrow docked tab.</summary>
    [AvaloniaFact]
    public void KeepsOneFullWidthColumn_WhenNotEvenTheMinimumFits()
    {
        Rect[] cards = Cards(Lay(240, items: 3, minItemWidth: 318, columnSpacing: 8));

        Assert.All(cards, c => Assert.Equal(240, c.Width, Tolerance));
        Assert.All(cards, c => Assert.Equal(0, c.X, Tolerance));
    }

    /// <summary>Empty columns are kept, not collapsed — CSS <c>auto-fill</c>, not <c>auto-fit</c>. One member in a
    /// wide window gets one card, not a card stretched across the whole row.</summary>
    [AvaloniaFact]
    public void KeepsEmptyColumns_RatherThanStretchingTheItemsThatAreThere()
    {
        Rect card = Assert.Single(Cards(Lay(1370, items: 1, minItemWidth: 318, columnSpacing: 8)));

        // Four columns fit at 1370; the card takes the first and the other three stay empty rather than being
        // folded into it. Its share is (1370 - 3x8) / 4 = 336.5, snapped to a whole pixel.
        Assert.InRange(card.Width, 336, 337);
        Assert.Equal(0, card.X, Tolerance);
    }

    /// <summary>
    /// Columns are placed by snapped EDGES, not by adding a rounded width over and over. An equal share is rarely a
    /// whole pixel, and rounding each width separately drifts: at 1370 that put the fourth card's right edge on 1371
    /// — a pixel past the panel it was supposed to be filling, which is the whitespace strip back in mirror image.
    /// Every boundary lands on a whole pixel, no two columns differ by more than one, and the last ends flush.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1370)]
    [InlineData(691)]
    [InlineData(1001)]
    [InlineData(1295)]
    public void SnapsItsColumnEdgesToWholePixels_WithoutDriftingPastThePanel(double width)
    {
        Rect[] cards = Cards(Lay(width, items: 8, minItemWidth: 318, columnSpacing: 8));
        Rect[] row = cards.Where(c => Math.Abs(c.Y - cards[0].Y) < Tolerance).ToArray();

        Assert.All(row, c => Assert.Equal(Math.Round(c.X), c.X, Tolerance));
        Assert.All(row, c => Assert.Equal(Math.Round(c.Width), c.Width, Tolerance));
        Assert.True(row.Max(c => c.Width) - row.Min(c => c.Width) <= 1,
            $"columns drifted more than a pixel apart at {width}");
        Assert.Equal(width, row[^1].Right, Tolerance);
    }

    /// <summary>Spacing is the panel's, not the card's: gaps sit only BETWEEN columns and rows, so the outer edges
    /// stay flush and a card template carries no layout arithmetic of its own.</summary>
    [AvaloniaFact]
    public void PutsItsSpacingBetweenTheCells_AndNotAroundThem()
    {
        FillGridPanel panel = Lay(970, items: 5, minItemWidth: 318, columnSpacing: 8, rowSpacing: 12, itemHeight: 176);
        Rect[] cards = Cards(panel);

        Assert.Equal(0, cards[0].X, Tolerance);
        Assert.Equal(0, cards[0].Y, Tolerance);
        Assert.Equal(8, cards[1].X - cards[0].Right, Tolerance);
        Assert.Equal(8, cards[2].X - cards[1].Right, Tolerance);
        Assert.Equal(12, cards[3].Y - cards[0].Bottom, Tolerance);

        // Two rows of 176 with one 12px gap, and nothing added below the last row.
        Assert.Equal(176 * 2 + 12, panel.DesiredSize.Height, Tolerance);
    }

    /// <summary>A row is as tall as the tallest card in it, and the next row starts below that — a card that grows
    /// must not be drawn over by the one beneath it.</summary>
    [AvaloniaFact]
    public void GivesEachRowTheHeightOfItsTallestCard()
    {
        var panel = new FillGridPanel { MinItemWidth = 100, ColumnSpacing = 0, RowSpacing = 0 };
        panel.Children.Add(new Border { Height = 50 });
        panel.Children.Add(new Border { Height = 90 });
        panel.Children.Add(new Border { Height = 40 });

        panel.Measure(new Size(200, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 200, panel.DesiredSize.Height));

        Assert.Equal(90, panel.Children[2].Bounds.Y, Tolerance);
        Assert.Equal(90 + 40, panel.DesiredSize.Height, Tolerance);
    }

    /// <summary>
    /// The rows start at the TOP of whatever rect the panel is handed, and the panel takes that whole rect. A control
    /// whose <c>ArrangeOverride</c> hands back LESS than it was given is centred in the remainder — Avalonia's
    /// <c>ArrangeCore</c> puts <c>VerticalAlignment.Stretch</c> on the same branch as <c>Center</c> — which parked a
    /// grid shorter than its viewport halfway down it, with a gap above the first row. Measure still reports the
    /// content height: that is what a scroller's extent is built from, and it must not be inflated to the viewport.
    /// </summary>
    [AvaloniaFact]
    public void TakesTheWholeHeightItIsGiven_SoShortContentStaysAtTheTop()
    {
        var panel = new FillGridPanel { MinItemWidth = 100, ColumnSpacing = 0, RowSpacing = 0 };
        for (var i = 0; i < 2; i++)
            panel.Children.Add(new Border { Height = 100 });

        panel.Measure(new Size(200, 500));
        panel.Arrange(new Rect(0, 0, 200, 500));

        Assert.Equal(100, panel.DesiredSize.Height, Tolerance);
        Assert.Equal(500, panel.Bounds.Height, Tolerance);
        Assert.Equal(0, panel.Bounds.Y, Tolerance);
        Assert.All(panel.Children, c => Assert.Equal(0, c.Bounds.Y, Tolerance));
    }

    /// <summary>The other side of the same rule: content taller than the rect must not be squashed into it, because
    /// the rows below the fold are what there is to scroll to.</summary>
    [AvaloniaFact]
    public void KeepsItsContentHeight_WhenTheRowsOutgrowTheRect()
    {
        var panel = new FillGridPanel { MinItemWidth = 100, ColumnSpacing = 0, RowSpacing = 0 };
        for (var i = 0; i < 8; i++)
            panel.Children.Add(new Border { Height = 100 });

        // Unbounded height, the way a ScrollContentPresenter measures a child it intends to scroll: a bounded
        // measure would have the framework clamp DesiredSize to the viewport before the panel is ever consulted.
        panel.Measure(new Size(200, double.PositiveInfinity));

        // Four rows of 100 in two columns — the measure the scroller turns into its extent.
        Assert.Equal(400, panel.DesiredSize.Height, Tolerance);

        // Arranged at its full extent, the way a ScrollContentPresenter arranges a child it cannot fit.
        panel.Arrange(new Rect(0, 0, 200, 400));
        Assert.Equal(400, panel.Bounds.Height, Tolerance);
        Assert.Equal(300, panel.Children[6].Bounds.Y, Tolerance);
    }

    /// <summary>An unbounded width has no leftover to divide, so every card falls back to its minimum instead of to
    /// infinity — a horizontal scroller must not be handed a card of NaN width.</summary>
    [AvaloniaFact]
    public void FallsBackToTheMinimum_WhenTheWidthIsUnbounded()
    {
        var panel = new FillGridPanel { MinItemWidth = 318, ColumnSpacing = 8 };
        for (var i = 0; i < 3; i++)
            panel.Children.Add(new Border { Height = 176 });

        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Assert.Equal(3 * 318 + 2 * 8, panel.DesiredSize.Width, Tolerance);
        Assert.Equal(176, panel.DesiredSize.Height, Tolerance);
    }
}
