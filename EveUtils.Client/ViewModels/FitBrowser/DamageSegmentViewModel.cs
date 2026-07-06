using Avalonia.Media;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One coloured damage-type chip on the per-module tooltip's damage line: the label (e.g. "EM 78")
/// in that damage type's colour, matching the DEFENSE panel's resist colours.</summary>
public sealed class DamageSegmentViewModel(string label, IBrush brush)
{
    public string Label { get; } = label;
    public IBrush Brush { get; } = brush;
}
