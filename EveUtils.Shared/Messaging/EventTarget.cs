namespace EveUtils.Shared.Messaging;

/// <summary>
/// Destination(s) of a single <see cref="IEventBus.PublishAsync"/> call. Flags: one publish can
/// go to <see cref="Local"/>, <see cref="Remote"/> or <see cref="Both"/> at once.
/// </summary>
[Flags]
public enum EventTarget
{
    /// <summary>In-process only, within this host (UI ⇄ services ⇄ modules). Default.</summary>
    Local = 1,

    /// <summary>The external server↔client bus only (future layer; no-op until a transport is wired).</summary>
    Remote = 2,

    /// <summary>Both local and the external bus.</summary>
    Both = Local | Remote
}
