using System;
using System.Collections.Generic;
using Avalonia;

namespace EveUtils.Client.Controls.Map;

/// <summary>Screen-space collision grid for one frame's labels: a label is only drawn where it overlaps nothing placed
/// before it, and each check looks at the few cells it covers rather than at every label so far.</summary>
internal sealed class MapLabelGrid
{
    private const double CellSize = 64;

    private readonly Dictionary<(int Column, int Row), List<Rect>> _cells = [];

    public void Clear() => _cells.Clear();

    /// <summary>Claims <paramref name="area"/> when it is free; false, and nothing claimed, when it overlaps.</summary>
    public bool TryPlace(Rect area)
    {
        (int firstColumn, int firstRow, int lastColumn, int lastRow) = _Span(area);
        for (int row = firstRow; row <= lastRow; row++)
        for (int column = firstColumn; column <= lastColumn; column++)
            if (_cells.TryGetValue((column, row), out List<Rect>? placed) && placed.Exists(other => other.Intersects(area)))
                return false;

        Reserve(area);
        return true;
    }

    /// <summary>Claims <paramref name="area"/> whatever is there already — for markers that are always drawn.</summary>
    public void Reserve(Rect area)
    {
        (int firstColumn, int firstRow, int lastColumn, int lastRow) = _Span(area);
        for (int row = firstRow; row <= lastRow; row++)
        for (int column = firstColumn; column <= lastColumn; column++)
        {
            if (!_cells.TryGetValue((column, row), out List<Rect>? placed))
                _cells[(column, row)] = placed = [];
            placed.Add(area);
        }
    }

    private static (int, int, int, int) _Span(Rect area) => (
        (int)Math.Floor(area.Left / CellSize), (int)Math.Floor(area.Top / CellSize),
        (int)Math.Floor(area.Right / CellSize), (int)Math.Floor(area.Bottom / CellSize));
}
