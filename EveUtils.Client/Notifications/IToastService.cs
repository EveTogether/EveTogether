using System;
using System.Collections.Generic;

namespace EveUtils.Client.Notifications;

/// <summary>
/// Shows transient, non-blocking toast notifications over the main window. A single app-wide seam so any view-model
/// can confirm an action without owning Avalonia notification plumbing. Before a host window is attached (headless
/// tests, early startup) <c>Show</c> is a silent no-op.
/// </summary>
public interface IToastService
{
    /// <summary>
    /// Shows a toast with a bold <paramref name="title"/> and an optional secondary <paramref name="message"/>.
    /// <paramref name="expiration"/> controls auto-dismiss: <c>null</c> uses the default (~5s); <see cref="TimeSpan.Zero"/>
    /// keeps it until the user dismisses it.
    /// </summary>
    void Show(string title, string? message = null, ToastKind kind = ToastKind.Success, TimeSpan? expiration = null);

    /// <summary>
    /// Shows a toast carrying one or more action buttons (e.g. "open metrics", or Accept / Decline). Each
    /// <see cref="ToastAction"/> renders as a button that runs its callback and then dismisses the toast. A toast with
    /// actions persists until the user picks one (or closes the card) rather than auto-expiring, so the choice isn't
    /// missed. An empty <paramref name="actions"/> list falls back to the plain
    /// <see cref="Show(string, string?, ToastKind, TimeSpan?)"/>.
    /// </summary>
    void Show(string title, string? message, ToastKind kind, IReadOnlyList<ToastAction> actions);
}
