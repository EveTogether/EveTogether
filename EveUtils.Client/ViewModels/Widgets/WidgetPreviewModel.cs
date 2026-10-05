using System.Collections.Generic;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using EveUtils.Client.LocalApi.Widgets;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>
/// What the widget page draws for a config, rebuilt on every change: the same placeholder as <c>widget-page.html</c>
/// (name in the accent, the field keys) with its theme, background and scale, so the preview shows what OBS gets.
/// </summary>
public sealed class WidgetPreviewModel
{
    private static readonly Color PanelColor = Color.FromRgb(13, 17, 23);

    private WidgetPreviewModel(WidgetConfig config)
    {
        Name = config.Name;
        Fields = config.Fields;
        Accent = new SolidColorBrush(Color.TryParse(config.Accent, out var accent) ? accent : Color.Parse(WidgetPresets.DefaultAccent));
        Background = config.Background is WidgetBackground.Panel
            ? new SolidColorBrush(PanelColor, config.PanelOpacity / 100.0)
            : Brushes.Transparent;
        Edge = config.Theme is WidgetTheme.Together ? new Thickness(3, 0, 0, 0) : new Thickness(0);
        Corner = config.Theme is WidgetTheme.Ticker ? new CornerRadius(0) : new CornerRadius(8);
        Orientation = config.Theme is WidgetTheme.Ticker ? Orientation.Horizontal : Orientation.Vertical;
        Scale = config.Scale / 100.0;
    }

    public string Name { get; }
    public IReadOnlyList<string> Fields { get; }
    public IBrush Accent { get; }
    public IBrush Background { get; }
    public Thickness Edge { get; }
    public CornerRadius Corner { get; }
    public Orientation Orientation { get; }
    public double Scale { get; }

    public static WidgetPreviewModel From(WidgetConfig config) => new(config);
}
