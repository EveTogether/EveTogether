using System;
using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Material.Icons;

namespace EveUtils.Client.Controls;

/// <summary>
/// A Material icon drawn straight from its path in the inherited text colour (ET-290) — the same glyphs as
/// <c>MaterialIcon</c>, without a template behind each one. A virtualised list builds several of these for every row it
/// scrolls in; each kind's path is parsed once for the whole app.
/// </summary>
public sealed class IconGlyph : Control
{
    /// <summary>Material paths are drawn on a 24 × 24 grid.</summary>
    private const double Grid = 24;

    private static readonly ConcurrentDictionary<MaterialIconKind, Geometry> Paths = new();

    public static readonly StyledProperty<MaterialIconKind> KindProperty =
        AvaloniaProperty.Register<IconGlyph, MaterialIconKind>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<IconGlyph>();

    static IconGlyph()
    {
        AffectsRender<IconGlyph>(KindProperty, ForegroundProperty);
    }

    public MaterialIconKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Foreground is not { } foreground || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        Geometry path = Paths.GetOrAdd(Kind, kind => Geometry.Parse(MaterialIconDataProvider.GetData(kind)));
        double scale = Math.Min(Bounds.Width, Bounds.Height) / Grid;
        Matrix placement = Matrix.CreateScale(scale, scale)
                           * Matrix.CreateTranslation((Bounds.Width - Grid * scale) / 2, (Bounds.Height - Grid * scale) / 2);
        using (context.PushTransform(placement))
            context.DrawGeometry(foreground, null, path);
    }
}
