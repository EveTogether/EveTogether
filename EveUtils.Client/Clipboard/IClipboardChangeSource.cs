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

    void Start();

    void Stop();
}

/// <summary>
/// Platforms with no clipboard-change notification. Unlike global shortcuts there is no XDG portal for this, and
/// the only alternative — polling — would have to remember the previous payload to tell "changed" from
/// "unchanged". Remembering unrecognised material is exactly what this feature promises never to do, so the
/// honest answer is to report it as unsupported rather than to quietly do nothing.
/// </summary>
public sealed class UnsupportedClipboardChangeSource : IClipboardChangeSource
{
    public bool IsSupported => false;

    public event Action? Changed
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
