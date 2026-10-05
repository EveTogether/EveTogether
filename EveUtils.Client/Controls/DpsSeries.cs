using System;
using System.Collections;
using System.Collections.Generic;
using Avalonia.Media;

namespace EveUtils.Client.Controls;

/// <summary>One line on the <see cref="DpsGraph"/>. The owner appends samples via <see cref="Add"/>; once the window
/// is full the oldest scroll off. Backed by a ring buffer so a long history window stays O(1) per frame instead of
/// shifting a list each tick. The lane is the line's unit — hp/s and GJ/s never share an axis (ET-277) — and a dashed
/// line is the giving half of a quantity whose receiving half is drawn solid in the same colour.</summary>
public sealed class DpsSeries
{
    private readonly SampleRing _values;

    public DpsSeries(IBrush stroke, int capacity, GraphLane lane = GraphLane.HitPoints, bool dashed = false)
        : this(stroke, new SampleRing(capacity), lane, dashed)
    {
    }

    /// <summary>A line drawn from a ring somebody else owns, so the Local API reads the very samples the graph shows.</summary>
    internal DpsSeries(IBrush stroke, SampleRing ring, GraphLane lane, bool dashed)
    {
        Stroke = stroke;
        _values = ring;
        Lane = lane;
        Dashed = dashed;
    }

    public IBrush Stroke { get; }

    public GraphLane Lane { get; }

    public bool Dashed { get; }

    /// <summary>Samples oldest→newest; the newest renders on the right ("now").</summary>
    public IReadOnlyList<double> Values => _values;

    public void Add(double value) => _values.Add(value);
}

/// <summary>Fixed-capacity ring of samples, exposed oldest→newest as a read-only list.</summary>
internal sealed class SampleRing(int capacity) : IReadOnlyList<double>
{
    private readonly double[] _buffer = new double[capacity];
    private int _start;
    private int _count;

    public int Count => _count;

    public double this[int index] => _buffer[(_start + index) % _buffer.Length];

    public void Add(double value)
    {
        lock (_buffer) _Add(value);
    }

    /// <summary>The newest sample and every <paramref name="stride"/>-th one before it, up to <paramref name="points"/>,
    /// oldest→newest. Locked, because the Local API reads from another thread than the one appending.</summary>
    public double[] Strided(int stride, int points)
    {
        lock (_buffer)
        {
            var available = _count == 0 ? 0 : (_count - 1) / stride + 1;
            var taken = Math.Min(points, available);
            var result = new double[taken];
            for (var i = 0; i < taken; i++)
                result[taken - 1 - i] = this[_count - 1 - i * stride];
            return result;
        }
    }

    private void _Add(double value)
    {
        if (_count < _buffer.Length)
        {
            _buffer[(_start + _count) % _buffer.Length] = value;
            _count++;
        }
        else
        {
            _buffer[_start] = value;
            _start = (_start + 1) % _buffer.Length;
        }
    }

    public IEnumerator<double> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
