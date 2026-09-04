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
public sealed record FleetOverviewLayoutState(
    bool IsWide,
    FleetBandDensity Density,
    int LanesPerRow,
    double LaneWidth,
    bool ShowLaneButtons)
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

    /// <summary>A lane this wide has room for STOP / LEAVE / START beside the clock.</summary>
    public const double LaneButtonsMinWidth = 300;

    /// <summary>The band's own horizontal padding, subtracted before lanes are measured against the width.</summary>
    public const double BandHorizontalPadding = 24;

    public const double LaneGap = 6;

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

        double inner = Math.Max(LaneMinWidth, contentWidth - BandHorizontalPadding);
        int perRow = Math.Max(1, (int)Math.Floor((inner + LaneGap) / (LaneMinWidth + LaneGap)));
        double laneWidth = Math.Floor((inner - LaneGap * (perRow - 1)) / perRow);

        int rows = pilotCount == 0 ? 0 : (int)Math.Ceiling(pilotCount / (double)perRow);
        var density = rows <= MaxLaneRows ? FleetBandDensity.Lanes : FleetBandDensity.Compact;

        return new FleetOverviewLayoutState(
            IsWide: contentWidth >= WideBreakpoint,
            density,
            perRow,
            laneWidth,
            ShowLaneButtons: laneWidth >= LaneButtonsMinWidth);
    }
}
