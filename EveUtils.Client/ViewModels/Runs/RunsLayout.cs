using System;

namespace EveUtils.Client.ViewModels.Runs;

/// <summary>
/// The widths the runs screen changes shape at (ET-291, ET-303). Read off the bounds of the module content — the
/// docked tab's column or the floating window — never off the app window, since <c>ModuleHostService</c> moves the
/// very same content between the two.
/// </summary>
public static class RunsLayout
{
    /// <summary>At and above this, the selected run reads in a pane beside the list; below it, in a drawer over it.
    /// Derived from the band RO-3 and RO-4 put above the list: a 400 px strip plus two filter blocks of two ~178 px
    /// tiles, their inner spacing and the gaps between them, is about 1204. Below that the list beside a 400 px pane
    /// is cramped as well. The two widths this screen is actually used at — 758 floating and 1303 docked — sit well
    /// clear on either side.</summary>
    public const double WideFrom = 1200;

    /// <summary>The pane beside the list, and the drawer's own width over it.</summary>
    public const double PaneWidth = 400;

    public const double DrawerWidth = 430;

    /// <summary>What the drawer leaves of the list behind it at the narrowest widths: enough to see that the list is
    /// still there and to click it closed.</summary>
    public const double DrawerMinimumGap = 64;

    /// <summary>The activity strip's widest column, as RO-3 drew it.</summary>
    public const double StripMaxWidth = 400;

    /// <summary>The narrowest the strip gets beside the filters: twelve weeks of 16 px cells, still wider than tall,
    /// and the legend with the ISK | runs switch on one line.</summary>
    public const double StripMinWidth = 260;

    /// <summary>Between the strip and a filter block, and between the two blocks — the band's own ColumnSpacing.</summary>
    public const double BandGap = 28;

    /// <summary>A filter tile with its full name: the widest measured headless (Abnoba Auscent, 166 px) plus room.
    /// A longer name is trimmed by the tile itself, with the name in its tooltip.</summary>
    public const double NamedTileWidth = 180;

    /// <summary>A compact tile — tick box, icon or portrait, count; the name in the tooltip — plus room for the
    /// block's own "n of m · show all" line.</summary>
    public const double CompactTileWidth = 100;

    /// <summary>The tile columns' own spacing inside a block (ColumnFlowPanel.ColumnSpacing).</summary>
    public const double TileSpacing = 10;

    /// <summary>
    /// How the band above the list is laid out for the width it was handed (ET-303), the first arrangement that fits:
    /// <list type="number">
    /// <item><b>Beside, two tile columns</b>: the strip (400 px, shrinking to 260) beside TYPES and CHARACTERS, each two
    /// named tile columns wide. The band is then the strip's own height.</item>
    /// <item><b>Beside, one tile column</b>: the strip beside the two blocks, one named column each.</item>
    /// <item><b>Beside, compact</b>: the strip beside two blocks of compact tiles — a deliberate, symmetric choice for
    /// both blocks; stacking would roughly double the band's height instead.</item>
    /// <item><b>Stacked</b>: the strip across the band, the two blocks side by side under it and sharing the whole
    /// width, compact. No column is ever held open beside anything.</item>
    /// </list>
    /// Every block is handed a share of the band's own width, never a fixed column past it, so a block can grow
    /// narrower than its tiles but never out of the window.
    /// </summary>
    public static RunsBandLayout Band(double bandWidth)
    {
        double width = Math.Max(0, bandWidth);
        double twoColumns = 2 * NamedTileWidth + TileSpacing;

        double strip = width - 2 * BandGap - 2 * twoColumns;
        if (strip >= StripMinWidth)
            return new RunsBandLayout(RunsBandKind.Beside, Math.Min(StripMaxWidth, strip), CompactTiles: false);

        strip = width - 2 * BandGap - 2 * NamedTileWidth;
        if (strip >= StripMinWidth)
            return new RunsBandLayout(RunsBandKind.Beside, Math.Min(StripMaxWidth, strip), CompactTiles: false);

        strip = width - 2 * BandGap - 2 * CompactTileWidth;
        if (strip >= StripMinWidth)
            return new RunsBandLayout(RunsBandKind.Beside, Math.Min(StripMaxWidth, strip), CompactTiles: true);

        return new RunsBandLayout(RunsBandKind.Stacked, width, CompactTiles: true);
    }

    /// <summary>The day header's own left margin (14 px each side) plus its caret column (ET-304).</summary>
    public const double DayLeadIn = 14 * 2 + 18;

    /// <summary>The gap between every column of the day header's totals (its own ColumnSpacing).</summary>
    public const double DayColumnSpacing = 8;

    /// <summary>The weekday and date together with a comfortable reading width — measured headless, "WEDNESDAY" (79)
    /// plus its 8 px gap plus "16 SEPTEMBER" (95), the longest weekday and the longest date this app ever shows,
    /// rounded up. Below this the pair keeps its own place (it is the Grid's star column, never a fixed one) but
    /// starts trimming with a tooltip, same as a filter tile's name.</summary>
    public const double DayLeadWidth = 190;

    /// <summary>The activity count, right-aligned — measured headless, "185 activities" (85 px) rounded up, the
    /// widest figure Jithran's own ET-303 screenshot showed.</summary>
    public const double DayCountWidth = 88;

    /// <summary>The flown time, right-aligned — measured headless, "27:46:11 flown" (85 px) rounded up, the same
    /// figure ET-303 measured. The first column to give way when the header is narrow: redundant with the count
    /// for "did I do more that day", and the only one of the two Jithran did not ask to keep unconditionally.</summary>
    public const double DayFlownWidth = 88;

    /// <summary>The source bar beside the ISK figure — unchanged from before ET-304, its own column now rather than
    /// an overlay. The second column to give way, once the flown time alone was not enough room.</summary>
    public const double DayBarWidth = 48;

    /// <summary>The net ISK figure, right-aligned — measured headless, "-999.99B ISK net" (97 px) rounded up, wider
    /// than any signed compact figure this app can produce. Never given up: it is what Jithran compares days by.
    /// "nothing recorded to value" (152 px) is wider still but rare enough that it trims with a tooltip instead of
    /// setting every day's column to a width the common case never needs.</summary>
    public const double DayIskWidth = 100;

    /// <summary>PUBLISH n LOCAL, in its own column at the far right — reserved at this width whether the button
    /// shows or not (ET-304 AC-1: a day without it must not let the column beside it grow into where it would sit).
    /// The button's own 90 px MinWidth plus the 14 px the header already keeps clear at its right edge.</summary>
    public const double DayLocalWidth = 104;

    /// <summary>
    /// The day header's totals, widest first (ET-304): every column but the weekday/date is a fixed width shared by
    /// every day, so "1 activity" and "46 activities" still start at the same x. Narrower than
    /// <see cref="DayLeadIn"/> plus every column plus <see cref="DayLeadWidth"/> drops the flown time first, then
    /// the source bar — the weekday and date keep their place regardless, a star column that only ever loses reading
    /// comfort, never overlaps anything.
    /// </summary>
    public static DayHeaderTier DayHeader(double width)
    {
        double fixedWidth = DayLeadIn + DayColumnSpacing + DayCountWidth + DayColumnSpacing + DayFlownWidth
            + DayColumnSpacing + DayBarWidth + DayColumnSpacing + DayIskWidth + DayLocalWidth;
        if (width >= fixedWidth + DayLeadWidth)
            return DayHeaderTier.Full;

        fixedWidth -= DayFlownWidth + DayColumnSpacing;
        if (width >= fixedWidth + DayLeadWidth)
            return DayHeaderTier.NoFlown;

        return DayHeaderTier.NoBar;
    }
}

/// <summary>Which of the day header's totals columns show, widest first (ET-304).</summary>
public enum DayHeaderTier
{
    /// <summary>Count, flown time, the source bar and the ISK figure all show.</summary>
    Full,

    /// <summary>The flown time is gone; the bar and the ISK figure keep their place.</summary>
    NoFlown,

    /// <summary>Flown time and the source bar are both gone; only the count and the ISK figure remain beside the
    /// weekday and date.</summary>
    NoBar
}

public enum RunsBandKind
{
    /// <summary>The strip in its own column, the two filter blocks beside it.</summary>
    Beside,

    /// <summary>The strip across the band, the two filter blocks under it.</summary>
    Stacked
}

/// <param name="StripWidth">The strip's column when <see cref="RunsBandKind.Beside"/>; the band's own width when
/// stacked.</param>
public readonly record struct RunsBandLayout(RunsBandKind Kind, double StripWidth, bool CompactTiles);
