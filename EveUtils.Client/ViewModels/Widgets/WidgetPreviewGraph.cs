using System.Collections.Generic;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>The DPS graph: its lines (lane baselines included), the lane labels and the figures under it.</summary>
public sealed record WidgetPreviewGraph(double Width, double Height, IReadOnlyList<WidgetPreviewLine> Lines,
    string HitPointsLabel, string? CapacitorLabel, double CapacitorTop, IReadOnlyList<WidgetPreviewNote> Legend);
