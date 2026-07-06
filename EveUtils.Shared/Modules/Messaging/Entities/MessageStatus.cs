namespace EveUtils.Shared.Modules.Messaging.Entities;

/// <summary>
/// Lifecycle of a queued message. Mail never lingers in <see cref="Delivered"/> — it is deleted once pushed.
/// An answerable invite goes <see cref="Pending"/> → <see cref="Delivered"/> on its first push, so it is delivered
/// exactly once (no longer re-pushed on every reconnect); it stays answerable until <see cref="Responded"/> or until
/// the expiry sweep removes it (<see cref="Expired"/> / abandoned).
/// </summary>
public enum MessageStatus
{
    Pending = 0,
    Delivered = 1,
    Responded = 2,
    Expired = 3
}
