using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace EveUtils.Client.Views;

/// <summary>
/// Shared borderless-window helpers. The OS draws no visible frame (clean, angular EVE look); this
/// restores edge/corner resize, forces square corners on Windows 11, and — on Windows — keeps the native
/// window styles alive so Aero Snap still works. All methods no-op gracefully off-Windows where relevant.
/// </summary>
internal static class WindowChrome
{
    private const int DwmwaWindowCornerPreference = 33; // DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE
    private const int DwmwcpDoNotRound = 1;             // DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND

    /// <summary>
    /// Makes a window visually borderless WITHOUT killing Windows snap. <c>WindowDecorations="None"</c> strips the
    /// Win32 styles snap needs (WS_CAPTION/WS_THICKFRAME/WS_MAXIMIZEBOX), so drag-to-edge docking, Win+arrows and
    /// Snap Layouts all die with it. On Windows the window therefore keeps FULL decorations and extends the client
    /// area over them — visually identical, but the native frame behaviours stay; the custom title bar carries the
    /// TitleBar element role so dragging it is a native caption drag (which is what triggers Aero Snap). Other
    /// platforms keep the plain borderless setup (manual drag/resize, unchanged for Linux/macOS).
    /// </summary>
    public static void ConfigureBorderless(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            window.WindowDecorations = WindowDecorations.None;
            return;
        }

        // BorderOnly, NOT Full: with the client area extended, Full makes Avalonia draw its own MANAGED title
        // text + caption buttons on top of our chrome (Window.ComputeDecorationParts only includes the managed
        // TitleBar part for Full). BorderOnly draws nothing managed while the Win32 styles snap needs stay:
        // WS_THICKFRAME/WS_MINIMIZEBOX/WS_MAXIMIZEBOX are decoration-mode-independent and WS_CAPTION is stripped
        // in extended mode anyway (verified against the Avalonia 12.0.4 Win32 WindowImpl source).
        window.WindowDecorations = WindowDecorations.BorderOnly;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = -1;

        // Maximized, the OS shifts the (invisible) frame off-screen; pad the content back into view.
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => window.Padding = window.OffScreenMargin);
        };
    }

    public static void ApplySquareCorners(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero) return;

        var preference = DwmwcpDoNotRound;
        DwmSetWindowAttribute(handle.Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    /// <summary>
    /// Restores edge/corner resizing on a frameless window (<c>SystemDecorations="None"</c>). A press within
    /// <paramref name="border"/> px of an edge starts an OS resize drag; the cursor reflects the hovered edge.
    /// Handlers are registered in the tunnel phase so they win over child controls that fill the window.
    /// </summary>
    public static void EnableBorderlessResize(Window window, double border = 6)
    {
        window.PointerMoved += (_, e) =>
        {
            if (window.WindowState != WindowState.Normal) { window.Cursor = null; return; }
            window.Cursor = CursorFor(EdgeAt(window, e.GetPosition(window), border));
        };

        window.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (window.WindowState != WindowState.Normal) return;
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;

            if (EdgeAt(window, e.GetPosition(window), border) is { } edge)
            {
                window.BeginResizeDrag(edge, e);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    private static WindowEdge? EdgeAt(Window window, Point p, double b)
    {
        var w = window.Bounds.Width;
        var h = window.Bounds.Height;
        bool left = p.X <= b, right = p.X >= w - b, top = p.Y <= b, bottom = p.Y >= h - b;

        if (top && left) return WindowEdge.NorthWest;
        if (top && right) return WindowEdge.NorthEast;
        if (bottom && left) return WindowEdge.SouthWest;
        if (bottom && right) return WindowEdge.SouthEast;
        if (left) return WindowEdge.West;
        if (right) return WindowEdge.East;
        if (top) return WindowEdge.North;
        if (bottom) return WindowEdge.South;
        return null;
    }

    private static Cursor? CursorFor(WindowEdge? edge) => edge switch
    {
        WindowEdge.West or WindowEdge.East => SizeWe,
        WindowEdge.North or WindowEdge.South => SizeNs,
        WindowEdge.NorthWest or WindowEdge.SouthEast => SizeNwse,
        WindowEdge.NorthEast or WindowEdge.SouthWest => SizeNesw,
        _ => null
    };

    private static readonly Cursor SizeWe = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor SizeNs = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor SizeNwse = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor SizeNesw = new(StandardCursorType.TopRightCorner);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
