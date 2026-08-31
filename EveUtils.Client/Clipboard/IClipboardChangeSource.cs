using System;
using System.Threading.Tasks;

namespace EveUtils.Client.Clipboard;

/// <summary>
/// One platform's way of being told that the clipboard changed, and — where the platform needs it — of reading it.
/// It never decides what to do with a payload: <see cref="ClipboardWatchService"/> decides whether to look at all.
/// Implementations are best-effort and must never throw.
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

    /// <summary>
    /// Reads the clipboard through the same channel this source is notified on, or null to let the caller read it
    /// through the toplevel instead.
    /// </summary>
    /// <remarks>
    /// Only a platform whose notification and whose reading disagree needs this. On Wayland they do: the change is
    /// seen over the compositor's data-control protocol while the toplevel reads the X11 selection, which a native
    /// Wayland owner is only mirrored into when something there asks for it.
    /// </remarks>
    Task<string?> ReadTextAsync() => Task.FromResult<string?>(null);
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
