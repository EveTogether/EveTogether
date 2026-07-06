using Avalonia.Media;

namespace EveUtils.Client.Controls;

/// <summary>
/// A subtle event marker on the DPS graph: a short coloured tick at the bottom axis. <see cref="Age"/>
/// is how many samples ago the event happened (0 = newest column); it ages with the scrolling window and is
/// dropped once it falls off the left edge, so markers stay aligned with the series.
/// </summary>
public sealed record GraphMarker(int Age, IBrush Brush);
