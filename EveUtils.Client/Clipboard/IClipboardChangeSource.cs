using System;

namespace EveUtils.Client.Clipboard;

/// <summary>
/// One platform's way of being told that the clipboard changed. An implementation never reads the clipboard
/// itself — it only reports that something was copied, and <see cref="ClipboardWatchService"/> decides whether
/// to look. Implementations are best-effort and must never throw.
/// </summary>
public interface IClipboardChangeSource : IDisposable
{
    /// <summary>
    /// False on platforms that cannot report a clipboard change. The UI shows this instead of leaving a toggle
    /// that silently does nothing.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>Raised off the UI thread on every clipboard change while started; subscribers marshal.</summary>
    event Action? Changed;

    /// <summary>
    /// Raised off the UI thread when <see cref="IsSupported"/> changes or the source stops notifying, which some
    /// platforms can only discover after <see cref="Start"/>.
    /// </summary>
    event Action? SupportChanged;

    void Start();

    void Stop();
}

/// <summary>
/// What is left once Windows and Wayland are served: macOS, and a Linux session without Wayland — an X11-only
/// session, where <c>wl-paste</c> has no compositor to talk to.
/// </summary>
/// <remarks>
/// Both have a route (macOS through <c>NSPasteboard.changeCount</c>, a monotonic counter, and X11 through
/// XFixes selection notifications), but neither is built here because neither could be measured — see
/// <c>docs/clipboard.md</c>. Polling on content stays ruled out: remembering unrecognised material to tell
/// "changed" from "unchanged" is exactly what this feature promises never to do.
/// </remarks>
public sealed class UnsupportedClipboardChangeSource : IClipboardChangeSource
{
    public bool IsSupported => false;

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public event Action? SupportChanged
    {
        add { }
        remove { }
    }

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}
