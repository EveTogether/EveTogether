using System;
using System.Collections.Generic;
using EveUtils.Shared.Modules.Map.Dtos;

namespace EveUtils.Client.Controls.Map;

/// <summary>Buckets the systems by world position once per graph, so finding the one under the pointer looks at a few
/// cells instead of every system on each mouse move.</summary>
internal sealed class MapHitGrid
{
    private const double CellSize = 100;

    private readonly MapGraphDto _graph;
    private readonly int _columns;
    private readonly int _rows;
    private readonly List<int>[] _cells;

    public MapHitGrid(MapGraphDto graph)
    {
        _graph = graph;
        _columns = (int)(graph.Width / CellSize) + 1;
        _rows = (int)(graph.Height / CellSize) + 1;
        _cells = new List<int>[_columns * _rows];
        foreach (MapSystemDto system in graph.Systems)
        {
            int cell = _CellOf(system.X, system.Y);
            (_cells[cell] ??= []).Add(system.Index);
        }
    }

    /// <summary>The system nearest to the world point within <paramref name="radius"/> world units, or -1.</summary>
    public int Nearest(double x, double y, double radius)
    {
        int best = -1;
        double bestDistance = radius * radius;
        int firstColumn = _Column(x - radius), lastColumn = _Column(x + radius);
        int firstRow = _Row(y - radius), lastRow = _Row(y + radius);
        for (int row = firstRow; row <= lastRow; row++)
        for (int column = firstColumn; column <= lastColumn; column++)
        {
            if (_cells[row * _columns + column] is not { } cell)
                continue;
            foreach (int index in cell)
            {
                MapSystemDto system = _graph.Systems[index];
                double dx = system.X - x, dy = system.Y - y, distance = dx * dx + dy * dy;
                if (distance >= bestDistance)
                    continue;
                bestDistance = distance;
                best = index;
            }
        }
        return best;
    }

    private int _CellOf(double x, double y) => _Row(y) * _columns + _Column(x);

    private int _Column(double x) => Math.Clamp((int)(x / CellSize), 0, _columns - 1);

    private int _Row(double y) => Math.Clamp((int)(y / CellSize), 0, _rows - 1);
}
