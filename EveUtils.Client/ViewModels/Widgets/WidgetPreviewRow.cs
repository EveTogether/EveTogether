using Avalonia.Media;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>
/// One line with text at both ends. <see cref="Badge"/> (KILL / LOSS) goes in front of the left text. The left text is
/// trimmed when the line runs out of room, unless it is a <see cref="LeftIsLabel"/> such as "BEST DROP": then the value is.
/// </summary>
public sealed record WidgetPreviewRow(string Left, string Right, IBrush LeftBrush, IBrush RightBrush,
    double LeftFontSize = 12, double RightFontSize = 12, string? Badge = null, IBrush? BadgeBrush = null, bool LeftIsLabel = false)
{
    public bool HasBadge => Badge is not null;
}
