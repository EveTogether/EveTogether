using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EveUtils.Client.Views.Skills;

/// <summary>The TRAINING QUEUE timeline of mockup v5: one bar from now to the end of the queue, with a tick where each
/// level ends (<see cref="Boundaries"/>, fractions 0-1 of the whole queue).</summary>
public sealed class QueueTimelineView : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> BoundariesProperty =
        AvaloniaProperty.Register<QueueTimelineView, IReadOnlyList<double>?>(nameof(Boundaries));

    static QueueTimelineView()
    {
        AffectsRender<QueueTimelineView>(BoundariesProperty);
    }

    public IReadOnlyList<double>? Boundaries { get => GetValue(BoundariesProperty); set => SetValue(BoundariesProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, 10);

    public override void Render(DrawingContext context)
    {
        var accent = this.TryFindResource("AccentBrush", ActualThemeVariant, out var a) && a is IBrush ab ? ab : Brushes.SteelBlue;
        var tick = this.TryFindResource("WindowBackgroundBrush", ActualThemeVariant, out var t) && t is IBrush tb ? tb : Brushes.Black;
        var bar = new Rect(0, 2, Bounds.Width, 6);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), null, bar);
        context.DrawRectangle(accent, null, bar);
        foreach (var boundary in Boundaries ?? [])
        {
            double x = boundary * Bounds.Width;
            context.DrawRectangle(tick, null, new Rect(x - 0.5, 2, 1.5, 6));
        }
    }
}
