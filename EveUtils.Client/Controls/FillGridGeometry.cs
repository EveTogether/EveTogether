using System;
using Avalonia.Layout;

namespace EveUtils.Client.Controls;

/// <summary>
/// Where the columns of a fill-grid fall. CSS's <c>repeat(auto-fill, minmax(&lt;min&gt;, 1fr))</c> as arithmetic:
/// as many columns of at least <c>minItemWidth</c> as the available width allows, with the remainder divided over
/// them so nothing is left as a strip of whitespace on the right.
///
/// It lives apart from the panels because there are two of them — <see cref="FillGridPanel"/> and
/// <see cref="VirtualizingFillGridPanel"/> — and the pixel-snapping below is the kind of thing that is right once
/// and then drifts apart in a copy (ET-108 found it the hard way).
/// </summary>
internal static class FillGridGeometry
{
    /// <summary>How many columns of at least <paramref name="minItemWidth"/> fit in <paramref name="available"/>.
    /// Never fewer than one: below the minimum the single card takes the full width rather than being clipped to a
    /// size nothing was designed for. Empty columns are counted (CSS <c>auto-fill</c>), so a lone card in a wide host
    /// stays one column wide.</summary>
    internal static int ColumnCount(double available, double minItemWidth, double columnSpacing, int childCount)
    {
        if (!double.IsFinite(available))
            return Math.Max(1, childCount);

        double min = Math.Max(1, minItemWidth);
        return Math.Max(1, (int)Math.Floor((available + columnSpacing) / (min + columnSpacing)));
    }

    /// <summary>The left edge of a column, snapped to whole device pixels. Columns are placed by their EDGES rather
    /// than by repeatedly adding a fractional width: an equal share is rarely a whole pixel, and a width rounded
    /// once per column and then added up drifts — enough to push the last column past the panel it is filling, which
    /// is the whitespace strip back again in mirror image. Snapping the edges instead keeps every boundary crisp,
    /// spreads the leftover fraction over the columns (never more than a pixel apart) and lands the last column
    /// exactly on the panel's right edge.</summary>
    internal static double Edge(int index, int columns, double available, double minItemWidth, double columnSpacing,
        bool useLayoutRounding, double scale)
    {
        if (index >= columns)
            return available;

        if (!double.IsFinite(available))
            return index * (Math.Max(1, minItemWidth) + columnSpacing);

        double edge = index * (available + columnSpacing) / columns;
        return useLayoutRounding ? LayoutHelper.RoundLayoutValue(edge, scale) : edge;
    }

    /// <summary>A column's width: up to the next column's edge, minus the gap that goes between them. The last
    /// column runs to the panel's edge, so no gap is taken off it.</summary>
    internal static double ColumnWidth(int index, int columns, double available, double minItemWidth,
        double columnSpacing, bool useLayoutRounding, double scale)
    {
        if (!double.IsFinite(available))
            return Math.Max(1, minItemWidth);

        double gap = index == columns - 1 ? 0 : columnSpacing;
        return Math.Max(0,
            Edge(index + 1, columns, available, minItemWidth, columnSpacing, useLayoutRounding, scale)
            - gap
            - Edge(index, columns, available, minItemWidth, columnSpacing, useLayoutRounding, scale));
    }
}
