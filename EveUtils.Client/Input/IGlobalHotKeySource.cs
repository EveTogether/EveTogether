using System;
using Avalonia.Input;
using EveUtils.Shared.Messaging;

namespace EveUtils.Client.Input;

/// <summary>
/// One platform's way of claiming a keyboard shortcut system-wide (ET-320), so it fires with the app in the
/// background — EVE Online in the foreground, say. Mirrors <see cref="Clipboard.IClipboardChangeSource"/>'s shape:
/// implementations are best-effort and must never throw, and a claim another process already holds fails cleanly
/// rather than taking the app down with it.
/// </summary>
public interface IGlobalHotKeySource : IDisposable
{
    /// <summary>False on platforms with no system-wide hotkey API. The UI reflects this instead of offering a
    /// toggle that would silently do nothing.</summary>
    bool IsSupported { get; }

    /// <summary>Raised off the UI thread whenever the currently registered combination is pressed anywhere in the
    /// system, focus notwithstanding.</summary>
    event Action? Pressed;

    /// <summary>
    /// Claims <paramref name="gesture"/> system-wide, replacing whatever this source currently holds. Fails (no
    /// throw) when another process already owns the combination — the caller decides what that means for the
    /// in-app, focus-only fallback, which never depends on this succeeding.
    /// </summary>
    Result Register(KeyGesture gesture);

    /// <summary>Releases whatever this source currently holds. A no-op when nothing is registered — called once
    /// there is no longer anything for the combination to save, so it is never held longer than it is needed.</summary>
    void Unregister();
}

/// <summary>What every platform besides Windows gets today: no global claim at all, and — per ET-320's own
/// acceptance — no error for it either. <see cref="GlobalSaveRunHotKeyService"/> checks <see cref="IsSupported"/>
/// before ever calling <see cref="Register"/>, so the fallback the two abstract members return here for
/// correctness is not expected to be reached in practice.</summary>
public sealed class UnsupportedGlobalHotKeySource : IGlobalHotKeySource
{
    public bool IsSupported => false;

    public event Action? Pressed
    {
        add { }
        remove { }
    }

    public Result Register(KeyGesture gesture) => Result.Failure(new ResultMessage(MessageSeverity.Error,
        MessageCodes.HotKeyUnavailable, "Global shortcuts are not supported on this platform.", "Shortcuts"));

    public void Unregister()
    {
    }

    public void Dispose()
    {
    }
}
