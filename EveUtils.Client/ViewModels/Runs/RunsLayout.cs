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
