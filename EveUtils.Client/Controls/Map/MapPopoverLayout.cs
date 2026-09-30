using System;
using Avalonia;

namespace EveUtils.Client.Controls.Map;

/// <summary>Where the hover popover goes (ET-399): down and to the right of the system, flipped to the other side of it
/// where that would leave the map, and pushed back inside where it still does not fit.</summary>
public static class MapPopoverLayout
{
    public const double Gap = 14;

    public static Rect Place(Point system, Size popover, Size map)
    {
        double x = system.X + Gap + popover.Width <= map.Width ? system.X + Gap : system.X - Gap - popover.Width;
        double y = system.Y + Gap + popover.Height <= map.Height ? system.Y + Gap : system.Y - Gap - popover.Height;
        return new Rect(
            new Point(Math.Max(0, Math.Min(x, map.Width - popover.Width)), Math.Max(0, Math.Min(y, map.Height - popover.Height))),
            popover);
    }
}
