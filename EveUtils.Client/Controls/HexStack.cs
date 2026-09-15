using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EveUtils.Client.Controls;

/// <summary>A face <see cref="HexStack"/> can draw: a portrait when there is one, the initial otherwise, and a change
/// notice when the portrait lands.</summary>
public interface IHexFace : INotifyPropertyChanged
{
    IImage? Portrait { get; }

    string Initial { get; }
}

/// <summary>
/// A crew as overlapping hexagons (ET-290), each ringed in the panel's colour against the one it overlaps. One control
/// drawing every face rather than an items control of portraits: a list row carries one of these, and a virtualised
/// list builds rows again each time it scrolls. A portrait that lands later redraws the stack; nothing else does.
/// </summary>
public sealed class HexStack : Control
{
    public static readonly StyledProperty<IReadOnlyList<IHexFace>?> FacesProperty =
        AvaloniaProperty.Register<HexStack, IReadOnlyList<IHexFace>?>(nameof(Faces));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<HexStack, double>(nameof(Size), 16);

    /// <summary>How far each face slides under the one after it.</summary>
    public static readonly StyledProperty<double> OverlapProperty =
        AvaloniaProperty.Register<HexStack, double>(nameof(Overlap), 5);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<HexStack, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<HexStack, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<IBrush?> RingProperty =
        AvaloniaProperty.Register<HexStack, IBrush?>(nameof(Ring));

    public static readonly StyledProperty<double> RingThicknessProperty =
        AvaloniaProperty.Register<HexStack, double>(nameof(RingThickness), 1);

    private IReadOnlyList<IHexFace> _watched = [];

    static HexStack()
    {
        AffectsMeasure<HexStack>(FacesProperty, SizeProperty, OverlapProperty);
        AffectsRender<HexStack>(FacesProperty, FillProperty, ForegroundProperty, RingProperty, RingThicknessProperty);
    }

    public IReadOnlyList<IHexFace>? Faces
    {
        get => GetValue(FacesProperty);
        set => SetValue(FacesProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double Overlap
    {
        get => GetValue(OverlapProperty);
        set => SetValue(OverlapProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? Ring
    {
        get => GetValue(RingProperty);
        set => SetValue(RingProperty, value);
    }

    public double RingThickness
    {
        get => GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = Faces?.Count ?? 0;
        return count == 0
            ? default
            : new Size(Size + (count - 1) * (Size - Overlap), HexPortrait.HeightFor(Size));
    }

    public override void Render(DrawingContext context)
    {
        if (Faces is not { } faces)
            return;

        double x = 0;
        foreach (IHexFace face in faces)
        {
            HexPortrait.Draw(context, new Point(x, 0), Size, face.Portrait, face.Initial, Fill, Foreground, Ring, RingThickness);
            x += Size - Overlap;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FacesProperty)
            _Watch();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _Watch();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _Unwatch();
    }

    /// <summary>Only while on screen: a face outlives every row that draws it, and must not keep a recycled one alive.</summary>
    private void _Watch()
    {
        _Unwatch();
        if (VisualRoot is null || Faces is not { } faces)
            return;

        _watched = faces;
        foreach (IHexFace face in _watched)
            face.PropertyChanged += _OnFaceChanged;
    }

    private void _Unwatch()
    {
        foreach (IHexFace face in _watched)
            face.PropertyChanged -= _OnFaceChanged;
        _watched = [];
    }

    private void _OnFaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IHexFace.Portrait))
            InvalidateVisual();
    }
}
