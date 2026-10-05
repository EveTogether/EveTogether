using System.Collections.Generic;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>Figures side by side in a three-column grid.</summary>
public sealed record WidgetPreviewStats(IReadOnlyList<WidgetPreviewFigure> Figures);
