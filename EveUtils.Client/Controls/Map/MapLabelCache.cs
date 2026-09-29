using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

namespace EveUtils.Client.Controls.Map;

/// <summary>
/// Every map label drawn once — text plus its halo — into a small bitmap, so a frame blits a picture per label
/// instead of laying out and rasterising five runs of glyphs. Measured on the real map (ET-392): labels were 25–45 ms
/// of a full frame drawn as text, the rest of the map under 17 ms.
/// </summary>
internal sealed class MapLabelCache : IDisposable
{
    /// <summary>Room around the text for its halo, in device-independent pixels.</summary>
    public const double Inset = 1;

    // Enough for every label of the map at once; past it the cache starts over rather than grow without bound.
    private const int Capacity = 8000;

    private static readonly IImmutableBrush HaloBrush = new ImmutableSolidColorBrush(MapPalette.Background);
    private static readonly Vector[] HaloOffsets = [new(-1, -1), new(1, -1), new(-1, 1), new(1, 1)];

    private readonly Dictionary<Key, MapLabel> _labels = [];
    private double _scaling = 1;

    public MapLabel Get(string text, MapLabelFont font, Color colour, FontFamily family, double scaling)
    {
        if (scaling != _scaling)
        {
            Clear();
            _scaling = scaling;
        }

        var key = new Key(text, font, colour);
        if (_labels.TryGetValue(key, out MapLabel? label))
            return label;
        if (_labels.Count >= Capacity)
            Clear();

        (double size, FontWeight weight, FontStyle style) = font switch
        {
            MapLabelFont.Region => (13, FontWeight.SemiBold, FontStyle.Normal),
            MapLabelFont.RegionLarge => (15, FontWeight.SemiBold, FontStyle.Normal),
            MapLabelFont.Faction => (11, FontWeight.Normal, FontStyle.Italic),
            MapLabelFont.Constellation => (11.5, FontWeight.Normal, FontStyle.Italic),
            MapLabelFont.SystemName => (12, FontWeight.Normal, FontStyle.Normal),
            MapLabelFont.SystemSecurity => (11, FontWeight.SemiBold, FontStyle.Normal),
            _ => (11.5, FontWeight.SemiBold, FontStyle.Normal)
        };
        var typeface = new Typeface(family, style, weight);
        var ink = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size,
            new ImmutableSolidColorBrush(colour));
        var halo = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, HaloBrush);

        var textSize = new Size(ink.WidthIncludingTrailingWhitespace, ink.Height);
        var image = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling((textSize.Width + Inset * 2) * scaling), (int)Math.Ceiling((textSize.Height + Inset * 2) * scaling)),
            new Vector(96 * scaling, 96 * scaling));
        using (DrawingContext context = image.CreateDrawingContext())
        {
            var origin = new Point(Inset, Inset);
            foreach (Vector offset in HaloOffsets)
                context.DrawText(halo, origin + offset);
            context.DrawText(ink, origin);
        }

        label = new MapLabel(image, textSize);
        _labels[key] = label;
        return label;
    }

    public void Clear()
    {
        foreach (MapLabel label in _labels.Values)
            label.Image.Dispose();
        _labels.Clear();
    }

    public void Dispose() => Clear();

    private readonly record struct Key(string Text, MapLabelFont Font, Color Colour);
}
