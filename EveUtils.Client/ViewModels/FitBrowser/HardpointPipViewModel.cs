using Avalonia;
using Avalonia.Media;

namespace EveUtils.Client.ViewModels.FitBrowser;

/// <summary>One turret- or launcher-hardpoint indicator dot above the high-slot ring, mirroring the in-game wheel:
/// turret hardpoints sit top-left, launcher hardpoints top-right. A used hardpoint (a turret/launcher is fitted) shows
/// as a bright filled dot; a free one as a dim hollow ring. The dot is an absolute-positioned geometry on the wheel
/// canvas (the same approach as the gauges/slot tiles, so it sits at its real coordinates rather than the canvas origin).</summary>
public sealed class HardpointPipViewModel
{
    // The in-game hardpoint indicators are a subtle grey (sampled ~#ADB1B5), not bright white.
    private static readonly SolidColorBrush UsedBrush = new(Color.Parse("#B4B8BC"));

    public Geometry Dot { get; }
    public IBrush Fill { get; }
    public double Opacity { get; }
    public string Tooltip { get; }

    public HardpointPipViewModel(double left, double top, double size, bool used, string tooltip)
    {
        Dot = new EllipseGeometry(new Rect(left, top, size, size));
        Fill = used ? UsedBrush : Brushes.Transparent;   // used = filled grey disc, free = hollow grey ring (stroke only)
        Opacity = used ? 0.95 : 0.7;
        Tooltip = tooltip;
    }
}
