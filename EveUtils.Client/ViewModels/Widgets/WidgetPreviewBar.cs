using System.Collections.Generic;

namespace EveUtils.Client.ViewModels.Widgets;

/// <summary>A thin bar of coloured pieces: a fill on its track, or a split per tier.</summary>
public sealed record WidgetPreviewBar(IReadOnlyList<WidgetPreviewSegment> Segments, double Height, double Gap);
