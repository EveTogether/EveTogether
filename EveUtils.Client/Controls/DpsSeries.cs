using System.Collections;
using System.Collections.Generic;
using Avalonia.Media;

namespace EveUtils.Client.Controls;

/// <summary>One line on the <see cref="DpsGraph"/>. The owner appends samples via <see cref="Add"/>; once the window
/// is full the oldest scroll off. Backed by a ring buffer so a long history window stays O(1) per frame instead of
/// shifting a list each tick.</summary>
public sealed class DpsSeries(IBrush stroke, int capacity)
{
    private readonly SampleRing _values = new(capacity);

    public IBrush Stroke { get; } = stroke;

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
