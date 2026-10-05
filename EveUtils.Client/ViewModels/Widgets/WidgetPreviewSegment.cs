using Avalonia.Media;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>One coloured piece of a <see cref="WidgetPreviewBar"/>, in pixels.</summary>
public sealed record WidgetPreviewSegment(double Width, IBrush Brush);
