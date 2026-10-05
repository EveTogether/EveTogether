using System.Collections.Generic;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>One line on the preview graph, in graph pixels.</summary>
public sealed record WidgetPreviewLine(IList<Point> Points, IBrush Brush, double Thickness, AvaloniaList<double>? Dashes);
