using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EveUtils.Client.Controls;

/// <summary>
/// A thin horizontal meter: a faint track and a filled part for <see cref="Fraction"/> of it. The fraction is
/// against a scale the owner chooses — on the fleet screen one scale for every card (ET-277), so two members' bars can
/// be compared by eye.
/// </summary>
public sealed class MeterBar : Control
{
    private static readonly IBrush DefaultTrack = new SolidColorBrush(Color.Parse("#0FFFFFFF"));

    public static readonly StyledProperty<double> FractionProperty =
        AvaloniaProperty.Register<MeterBar, double>(nameof(Fraction));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<MeterBar, IBrush?>(nameof(Fill));

    /// <summary>The bar's own faint background, styleable per owner (ET-285: MINING's share bar wants a track a
    /// reader can actually see, where the combat meters this control also draws want the near-invisible default).
    /// Defaults to the original always-faint brush so nothing changes for a caller that never sets it.</summary>
    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<MeterBar, IBrush?>(nameof(Track), DefaultTrack);

    static MeterBar()
    {
        AffectsRender<MeterBar>(FractionProperty, FillProperty, TrackProperty);
    }

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var track = new Rect(Bounds.Size);
        if (track.Width <= 0 || track.Height <= 0)
            return;

        var radius = Math.Min(2, track.Height / 2);
        context.DrawRectangle(Track, null, track, radius, radius);

        var width = Math.Clamp(Fraction, 0, 1) * track.Width;
        // Anything at all shows as at least a sliver: 3 GJ/s on a 40 GJ/s scale is not nothing.
        if (Fraction > 0 && width < 2)
            width = 2;
        if (width > 0 && Fill is { } fill)
            context.DrawRectangle(fill, null, new Rect(0, 0, width, track.Height), radius, radius);
    }
}
