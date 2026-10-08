using Avalonia.Media;

namespace EveUtils.Client.ViewModels.Runs.Sections;

/// <summary>
/// One series in TIMELINE's legend (ET-468); a series the run never had a line for reads "none", never a flat zero.
/// </summary>
public sealed record CombatLegendItem(string Text, IBrush Ink, bool IsPresent)
{
    public double Opacity => IsPresent ? 1 : 0.45;
}
