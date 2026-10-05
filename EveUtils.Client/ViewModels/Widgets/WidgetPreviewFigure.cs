using Avalonia.Media;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>A label with its figure; <see cref="FontSize"/> tells a headline (30, 26, 18) from a stat in a grid (15).</summary>
public sealed record WidgetPreviewFigure(string Label, string Value, IBrush Brush, double FontSize);
