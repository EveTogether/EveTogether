using Avalonia.Media;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>A small line of text: a system, the crew, or a figure under the graph.</summary>
public sealed record WidgetPreviewNote(string Text, IBrush Brush);
