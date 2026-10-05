using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EveUtils.Client.Views.Skills;

/// <summary>
/// The five level pips of mockup v5, the same as in game: filled = trained, lit = training now, outlined in the accent =
/// queued, outlined dim = not yet, dotted = not injected. One control for CATALOGUE, TRAINING QUEUE, PLANS and the
/// detail pane, so every screen reads a level the same way.
/// </summary>
public sealed class SkillPipsView : Control
{
    public static readonly StyledProperty<int> CurrentLevelProperty = AvaloniaProperty.Register<SkillPipsView, int>(nameof(CurrentLevel));
    public static readonly StyledProperty<int> QueuedLevelProperty = AvaloniaProperty.Register<SkillPipsView, int>(nameof(QueuedLevel));
    public static readonly StyledProperty<int> TrainingLevelProperty = AvaloniaProperty.Register<SkillPipsView, int>(nameof(TrainingLevel));
    public static readonly StyledProperty<bool> IsInjectedProperty = AvaloniaProperty.Register<SkillPipsView, bool>(nameof(IsInjected), true);
    public static readonly StyledProperty<double> PipWidthProperty = AvaloniaProperty.Register<SkillPipsView, double>(nameof(PipWidth), 8);

    static SkillPipsView()
    {
        AffectsRender<SkillPipsView>(CurrentLevelProperty, QueuedLevelProperty, TrainingLevelProperty, IsInjectedProperty, PipWidthProperty);
        AffectsMeasure<SkillPipsView>(PipWidthProperty);
    }

    public int CurrentLevel { get => GetValue(CurrentLevelProperty); set => SetValue(CurrentLevelProperty, value); }

    /// <summary>The highest level waiting in the queue (0 when none).</summary>
    public int QueuedLevel { get => GetValue(QueuedLevelProperty); set => SetValue(QueuedLevelProperty, value); }

    /// <summary>The level training right now (0 when none).</summary>
    public int TrainingLevel { get => GetValue(TrainingLevelProperty); set => SetValue(TrainingLevelProperty, value); }

    public bool IsInjected { get => GetValue(IsInjectedProperty); set => SetValue(IsInjectedProperty, value); }

    public double PipWidth { get => GetValue(PipWidthProperty); set => SetValue(PipWidthProperty, value); }

    private double Gap => PipWidth / 4;
    private double PipHeight => PipWidth * 0.75;

    protected override Size MeasureOverride(Size availableSize) => new(5 * PipWidth + 4 * Gap, PipHeight + 1);

    public override void Render(DrawingContext context)
    {
        var accent = _Brush("AccentBrightBrush", Brushes.LightBlue);
        var dim = _Brush("TextDimBrush", Brushes.Gray);
        var bright = _Brush("TextBrightBrush", Brushes.White);
        double y = (Bounds.Height - PipHeight) / 2;
        for (int level = 1; level <= 5; level++)
        {
            var rect = new Rect((level - 1) * (PipWidth + Gap) + 0.5, y + 0.5, PipWidth - 1, PipHeight - 1);
            if (!IsInjected)
            {
                context.DrawRectangle(null, new Pen(dim, 1, new DashStyle([1, 2], 0)), rect);
            }
            else if (level <= CurrentLevel)
            {
                context.DrawRectangle(accent, null, rect);
            }
            else if (level == TrainingLevel)
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), new Pen(bright, 1), rect);
            }
            else if (level <= QueuedLevel)
            {
                context.DrawRectangle(null, new Pen(accent, 1), rect);
            }
            else
            {
                context.DrawRectangle(null, new Pen(dim, 1), rect);
            }
        }
    }

    private IBrush _Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
}
