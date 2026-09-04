using System;

namespace EveUtils.Client.ViewModels.Fleets;

/// <summary>How the character band draws its lanes: cards with a clock and buttons, or one line per character.</summary>
public enum FleetBandDensity
{
    Lanes,
    Compact,
}

/// <summary>
/// What the fleet overview draws at a given width (ET-170). The table has two states and the band two densities, and
/// both follow from the space the screen is handed rather than from the window it happens to sit in: the same
/// content is a 758 px tab on the start-up window, a 720 px floating window, or ~1578 px maximised on a 1920 screen,
/// and <c>ModuleHostService</c> moves that one content between them.
/// </summary>
/// <param name="IsWide">The table shows every column — your characters and the doctrine each get a column, and all
/// four actions stand on the row. Narrow folds those into a second line under the name and an overflow menu.</param>
/// <param name="Density">Cards while every lane still fits within two rows, else one line per character.</param>
/// <param name="LanesPerRow">How many lane cards stand beside each other.</param>
/// <param name="LaneWidth">The width every lane card is given, so a row of them fills the band edge to edge.</param>
/// <param name="ShowLaneButtons">Whether a lane card has room for its buttons beside the clock. Below that width the
/// actions live in the lane's context menu — the same trade the compact form makes.</param>
/// <param name="ActionsWidth">What the actions cell on a fleet row is given. It grows with the width rather than
/// standing at one number, because that is what decides whether JOIN keeps its place on the row.</param>
public sealed record FleetOverviewLayoutState(
    bool IsWide,
    FleetBandDensity Density,
    int LanesPerRow,
    double LaneWidth,
    bool ShowLaneButtons,
    double ActionsWidth)
{
    public bool IsCompactBand => Density == FleetBandDensity.Compact;
}

public static class FleetOverviewLayout
{
    /// <summary>
    /// The width from which the table gets its wide form back. Its fixed columns (status 74, members 92, FC 116,
    /// your characters 128, doctrine 132, since 90, actions 250, seven gaps of 10 and 28 of padding) come to 980 px,
    /// which leaves a fleet name 200 px at this breakpoint — the mockup's own drawing width. Anything narrower gets
    /// the narrow form, which the mockup lays out at 758.
    /// </summary>
    public const double WideBreakpoint = 1180;

    /// <summary>The narrowest a lane card may be. 239 is the width the mockup gives a lane at 758 px; a few pixels of
    /// slack keep three lanes on a row when the host trims a border or two off the nominal width.</summary>
    public const double LaneMinWidth = 236;

    /// <summary>A lane this wide has room for STOP / LEAVE / START beside the clock, the third line under the fleet
    /// name and the chips under the clock — the full lane of scherm 1. At the wide breakpoint the band fits three of
    /// these on a row and hands each 380 px, which is the width the mockup draws a lane at.</summary>
    public const double LaneButtonsMinWidth = 300;

    /// <summary>The band's own horizontal padding (both sides together), subtracted before lanes are measured against
    /// the width: 12 a side in the narrow state, 14 a side in the wide one — the same numbers Border.band carries.</summary>
    public const double NarrowBandPadding = 24;
    public const double WideBandPadding = 28;

    public const double LaneGap = 6;

    /// <summary>
    /// The actions cell on a fleet row. The narrow state has room for two buttons and an overflow (scherm 10); the
    /// wide one starts at the 250 px that holds scherm 1's four, and grows to 320 — enough for the heaviest row this
    /// screen can draw, your own started invite-only fleet at STOP · REQUEST · MANAGE · METRICS · SHARE · "⋯" = 311.
    /// It only grows out of width the fleet name does not need: every fixed column and gap of the wide table comes to
    /// 720 px, and the name is served first up to <see cref="NameFloor"/> — comfortably past the 210 px it has at the
    /// breakpoint — so the actions cell stays at its floor until the name has room to spare.
    /// </summary>
    public const double NarrowActionsWidth = 150;
    public const double MinActionsWidth = 250;
    public const double MaxActionsWidth = 320;
    public const double FixedColumnsWidth = 720;
    public const double NameFloor = 280;

    /// <summary>The band folds to one line per character once its lanes need more rows than this.</summary>
    public const int MaxLaneRows = 2;

    /// <summary>
    /// Resolves both states from the content's width and the number of this client's own characters. The pilot count
    /// is the roster, not the running set — an idle character keeps a lane (ET-131's rule), so it takes space too.
    /// </summary>
    public static FleetOverviewLayoutState Resolve(double contentWidth, int pilotCount)
    {
        if (double.IsNaN(contentWidth) || contentWidth <= 0)
            contentWidth = WideBreakpoint;

        bool isWide = contentWidth >= WideBreakpoint;
        double inner = Math.Max(LaneMinWidth, contentWidth - (isWide ? WideBandPadding : NarrowBandPadding));

        // Three forms, tried widest first — the band asks for the most it can have and gives up one thing at a time,
        // which is the order scherm 15 sets out. Full lanes with their buttons; failing that the slim lane of
        // scherm 10, which trades the buttons and the chips for a card that fits three to a row at 758; failing that
        // one line per character (scherm 13). Nothing here counts pilots: it counts rows.
        var roomy = Pack(inner, LaneButtonsMinWidth, pilotCount);
        var slim = Pack(inner, LaneMinWidth, pilotCount);
        bool showButtons = roomy.Rows <= MaxLaneRows && roomy.Width >= LaneButtonsMinWidth;
        var chosen = showButtons ? roomy : slim;

        var density = chosen.Rows <= MaxLaneRows ? FleetBandDensity.Lanes : FleetBandDensity.Compact;

        double actions = isWide
            ? Math.Clamp(contentWidth - FixedColumnsWidth - NameFloor, MinActionsWidth, MaxActionsWidth)
            : NarrowActionsWidth;

        return new FleetOverviewLayoutState(isWide, density, chosen.PerRow, chosen.Width, showButtons, actions);
    }

    /// <summary>How many lanes of at least <paramref name="minWidth"/> fit on a row of <paramref name="inner"/>, how
    /// wide each then becomes once the leftover is divided over them, and how many rows that costs.</summary>
    private static (int PerRow, double Width, int Rows) Pack(double inner, double minWidth, int pilotCount)
    {
        int fit = Math.Max(1, (int)Math.Floor((inner + LaneGap) / (minWidth + LaneGap)));
        int rows = pilotCount == 0 ? 0 : (int)Math.Ceiling(pilotCount / (double)fit);

        // Spread the lanes evenly over the rows they already cost rather than filling each row to the brim: six
        // pilots where five fit are two rows of three, not five and a lone card. Same number of rows either way, so
        // this is free — and it is the band scherm 1 draws.
        int perRow = rows > 1 ? (int)Math.Ceiling(pilotCount / (double)rows) : fit;
        double width = Math.Floor((inner - LaneGap * (perRow - 1)) / perRow);
        return (perRow, width, rows);
    }
}
